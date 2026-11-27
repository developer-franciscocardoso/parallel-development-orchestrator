using Pda.Admission;
using Pda.Agents;
using Pda.Application;
using Pda.Core;
using Pda.Persistence;
using Pda.Planning;
using Pda.Providers;
using Pda.Quality;
using Pda.Recovery;
using Pda.Repositories;
using Pda.Scheduling;
using Pda.Security;
using Pda.Verification;

namespace Pda.Hosting;

public sealed partial class OrchestratorHost
{
    public string Home { get; }
    public StateStore Store { get; }
    public PlanningService Planning { get; }
    public RecoveryService Recovery { get; }
    public QualityCoordinator Quality { get; }
    public ProviderRegistry Providers { get; }
    public HostSettings Settings { get; }
    private readonly ExecutionCoordinator workers;
    private readonly PlanExecution execution;
    private readonly CodexProfile? profile;

    public static OrchestratorHost Open(string home) => new(home);

    private OrchestratorHost(string path)
    {
        Home = HostFiles.DirectoryAt(path);
        TrustedPaths.OutsideRepository(Home);
        Settings = HostFiles.Initialize(Home);
        profile = HostFiles.Profile(Home, Settings);
        var evidence = HostFiles.DirectoryAt(Path.Combine(Home, "evidence"));
        var trees = HostFiles.DirectoryAt(Path.Combine(Home, "worktrees"));
        var database = Path.Combine(Home, "state.db");
        foreach (var suffix in new[] {"", "-wal", "-shm", ".startup.lock"})
            if (Path.Exists(database + suffix)) TrustedPaths.File(database + suffix);
        Store = new(database);
        Providers = ProviderRegistry.Load(TrustedPaths.File(
            Path.Combine(Home, "providers.json")), ["simulated", "codex"]);
        Providers.Resolve("simulated", "simulated");
        Planning = new(Store);
        Recovery = new(Store, new LocalWorkerInspector(), evidence);
        var runtimes = new Dictionary<string, IAgentRuntime>
        {
            ["simulated"] = new SimulatedRuntime(SimulationScenario.Success)
        };
        if (Settings.EnableLiveCodex)
        {
            Providers.Resolve("codex", Settings.CodexModel!);
            runtimes.Add("codex", new CodexRuntime(profile!));
        }
        var admission = new AdmissionService(Store, Providers);
        DispatchAssignment? Reserve(string plan)
        {
            if (IsCancelled(plan)) return null;
            var slot = admission.ReserveNext(plan, task => task.Runtime switch
            {
                "simulated" => "simulated",
                "codex" when Settings.EnableLiveCodex => Settings.CodexModel,
                _ => null
            }, Concurrency(plan));
            return slot == null ? null : new(slot.Contract, slot.Attempt,
                slot.Model.Model.Id);
        }

        var workspaces = new PlanWorkspaces(Store, trees);
        workers = new(Store, id => runtimes[id], Reserve, workspaces.Open,
            trees, evidence);
        var git = new GitRepositoryService();
        Quality = new(Store, new(git.CaptureCandidate, git.PrepareMerge,
            git.AdvancePlan), profile == null ? new MissingVerifier() :
            new SandboxedVerificationRunner(profile), new MechanicalReviewer(),
            evidence);
        execution = new(Store, workers, Quality.ProcessAsync);
    }

    private sealed class MissingVerifier : IVerificationRunner
    {
        public Task<CheckReport> RunAsync(VerificationInput input,
            CancellationToken token) => Task.FromResult(new CheckReport(
                input.Binding, false, [], "A native Codex MXC verifier is required."));
    }

    public DoctorReport Doctor()
    {
        Store.CheckIntegrity();
        var ready = OperatingSystem.IsWindows();
        var mode = Settings.EnableLiveCodex
            ? "Codex is enabled. Dispatch verifies authentication and confinement."
            : "Local simulation is enabled. Live Codex workers are disabled.";
        return new(ready, Settings.EnableLiveCodex, [
            "SQLite integrity checks passed. Git is available.", mode,
            ready ? "Windows process supervision is available." :
                "Native worker supervision requires Windows.",
            profile == null ? "Native MXC verifier is unavailable." :
                "Native Codex verifier found. Each check probes MXC confinement."
        ]);
    }
}
