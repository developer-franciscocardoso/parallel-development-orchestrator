using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pda.Core;
using Pda.Repositories;

namespace Pda.Planning;

public sealed class PlanningService(IStateStore store)
{
    public Artifact<ProjectProfile> InspectAndRegister(string projectId,
        string repository)
    {
        var before = store.LatestProjectProfile<ProjectProfile>(projectId);
        var profile = new ProjectDiscovery().Inspect(repository);
        store.RegisterProject(projectId, profile.Root);
        return store.SaveProjectProfile(projectId, profile, before?.Version ?? 0);
    }

    public IReadOnlyList<TaskContract> Generate(string projectId, string planId,
        string objective, string[] owned, string runtime, long tokenBudget,
        VerificationCommand[]? verification = null)
    {
        var profile = Current(projectId);
        ArgumentNullException.ThrowIfNull(owned);
        foreach (var path in owned) ContractValidator.ValidatePath(path);
        return Generate(projectId, planId, objective, owned, runtime, tokenBudget,
            verification, profile);
    }

    private IReadOnlyList<TaskContract> Generate(string projectId, string planId,
        string objective, string[] owned, string runtime, long tokenBudget,
        VerificationCommand[]? verification, Artifact<ProjectProfile> profile)
    {
        var commands = verification ?? profile.Data.Verification
            .Where(check => AppliesTo(check, owned, profile.Data.Evidence))
            .Select(c => c.Command).ToArray();
        if (commands.Length == 0)
            throw new InvalidOperationException(
                "Supply a verification executable and arguments for this scope.");
        var task = new TaskContract("T001", objective, [], owned, [], [],
            [objective], commands, [objective], Risk.Medium, 2, 1800, 0, runtime,
            tokenBudget);
        return Persist(projectId, profile, planId, tokenBudget, [task]);
    }

    public IReadOnlyList<TaskContract> Generate(string projectId, string planId,
        string objective, string runtime, long tokenBudget,
        VerificationCommand[]? verification = null)
    {
        var profile = Current(projectId);
        var owned = profile.Data.Evidence.Where(e =>
            e.Kind is "source" or "manifest" or "documentation")
            .Select(e => e.Path.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (owned.Length is 0 or > 32)
            throw new InvalidOperationException("Supply explicit task-owned paths.");
        return Generate(projectId, planId, objective, owned, runtime,
            tokenBudget, verification, profile);
    }

    private static bool AppliesTo(ProjectCheck check, string[] owned,
        SourceEvidence[] files)
    {
        var extensions = check.Command.Executable == "dotnet"
            ? new[] { ".cs", ".csproj" }
            : [".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs"];
        return files.Any(file =>
            (file.Path == check.Source || file.Kind == "source" &&
                extensions.Contains(Path.GetExtension(file.Path),
                    StringComparer.OrdinalIgnoreCase)) &&
            (check.Scope.Length == 0 ||
                ContractValidator.ContainsPath(check.Scope, file.Path)) &&
            owned.Any(p => ContractValidator.ContainsPath(p, file.Path)));
    }
    public IReadOnlyList<TaskContract> Import(string projectId, string planId,
        long tokenBudget, IReadOnlyList<TaskContract> tasks) =>
        Persist(projectId, Current(projectId), planId, tokenBudget, tasks);

    public IReadOnlyList<TaskContract> ImportJson(string projectId, string planId,
        long tokenBudget, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576)
            throw new InvalidDataException("Task plan exceeds 1 MiB.");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowDuplicateProperties = false, MaxDepth = 12,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<Risk>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        var tasks = JsonSerializer.Deserialize<TaskContract[]>(json, options);
        return Import(projectId, planId, tokenBudget,
            tasks ?? throw new InvalidDataException("Task plan must be an array."));
    }

    private Artifact<ProjectProfile> Current(string projectId)
    {
        var profile = store.LatestProjectProfile<ProjectProfile>(projectId)
            ?? throw new InvalidOperationException("Inspect the project first.");
        var head = new GitRepositoryService().DiscoveryHead(profile.Data.Root);
        if (head.Repository.CommonDirectory != profile.Data.GitCommonDirectory ||
            head.Commit != profile.Data.Commit)
            throw new InvalidOperationException("Repository changed. Refresh the profile.");
        return profile;
    }

    private IReadOnlyList<TaskContract> Persist(string id, Artifact<ProjectProfile> profile,
        string planId, long budget, IReadOnlyList<TaskContract> tasks)
    {
        ContractValidator.Validate(tasks);
        var instructions = profile.Data.Evidence.Where(e => e.Kind == "instruction")
            .Select(e => e.Path);
        var protectedPaths = instructions.Concat([".git", ".codex", ".agents",
            ".execution"]).ToArray();
        var contracts = tasks.Select(task => Protect(task, protectedPaths)).ToArray();
        store.CreatePlan(id, profile.Data.Root, planId, contracts, budget, profile.Version);
        return contracts;
    }

    private static TaskContract Protect(TaskContract task, string[] protectedPaths)
    {
        if (task.Owned.Any(owner => protectedPaths.Any(p =>
            ContractValidator.ContainsPath(p, owner))))
            throw new ArgumentException("Task owns an instruction or control path.");
        var excluded = protectedPaths.Where(p => task.Owned.Any(owner =>
            ContractValidator.ContainsPath(owner, p)));
        return task with { Excluded = task.Excluded.Concat(excluded)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
}
