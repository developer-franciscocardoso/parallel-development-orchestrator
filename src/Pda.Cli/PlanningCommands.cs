using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pda.Core;
using Pda.Hosting;
using Pda.Planning;

namespace Pda.Cli;

public static partial class CliApplication
{
    private static Artifact<ProjectProfile> Register(OrchestratorHost host,
        string project, string repository)
    {
        var path = Path.GetFullPath(repository);
        var latest = host.Store.LatestProjectProfile<ProjectProfile>(project);
        if (latest != null)
        {
            var head = new Pda.Repositories.GitRepositoryService().DiscoveryHead(path);
            if (head.Repository.CommonDirectory == latest.Data.GitCommonDirectory &&
                head.Repository.Root == latest.Data.Root &&
                head.Commit == latest.Data.Commit) return latest;
        }
        return host.Planning.InspectAndRegister(project, path);
    }

    private static object Create(OrchestratorHost host, Arguments a)
    {
        var project = a.Values[0];
        var plan = a.Values[1];
        var budget = a.Number("budget", 0, long.MaxValue);
        var tasks = a.Option("tasks");
        if (tasks != null)
        {
            if (new[] {"objective", "owned", "runtime", "checks"}.Any(a.Has))
                throw new UsageError("--tasks supplies complete task contracts.");
            return host.Planning.ImportJson(project, plan, budget, Read(tasks));
        }
        var objective = a.Required("objective");
        var runtime = a.Option("runtime") ?? "simulated";
        var owned = a.Many("owned");
        var checks = a.Option("checks") is string file ? Checks(file) : null;
        return owned.Length == 0
            ? host.Planning.Generate(project, plan, objective, runtime, budget,
                checks)
            : host.Planning.Generate(project, plan, objective, owned, runtime,
                budget, checks);
    }

    private static VerificationCommand[] Checks(string file)
    {
        var options = new JsonSerializerOptions(Json) {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false
        };
        return JsonSerializer.Deserialize<VerificationCommand[]>(Read(file),
            options) ?? throw new UsageError("--checks requires a JSON array.");
    }

    private static string Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 1_048_576)
            throw new UsageError("Input file exceeds 1 MiB.");
        var buffer = new byte[(int)file.Length];
        file.ReadExactly(buffer);
        if (file.ReadByte() != -1)
            throw new IOException("Input file changed while reading.");
        var text = new UTF8Encoding(false, true).GetString(buffer);
        return text.TrimStart('\uFEFF');
    }
}
