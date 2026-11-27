using System.Diagnostics;
using Pda.Admission;
using Pda.Agents;
using Pda.Application;
using Pda.Core;
using Pda.Execution;
using Pda.Persistence;
using Pda.Planning;
using Pda.Providers;
using Pda.Quality;
using Pda.Recovery;
using Pda.Repositories;
using Pda.Scheduling;
using Pda.Verification;

namespace Pda.Benchmarks;

internal sealed partial class BenchFixture : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("pda-bench-").FullName;
    private string Repo => Folder("repo");
    private string Trees => Folder("trees");
    private string Logs => Folder("evidence");
    private string Database => Path.Combine(root, "state.sqlite");
    private string Folder(string name) =>
        Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    private readonly GitRepositoryService git = new();
    private static HostPressure Capacity() => new(1, 1, 4L << 30, 100);

    internal async Task RunAsync(Measurements data, bool integrate,
        CancellationToken token)
    {
        await InitializeGit(token);
        var store = new StateStore(Database);
        var plan = new PlanningService(store);
        plan.InspectAndRegister("fixture", Repo);
        var providers = Providers();
        var baseline = git.Register(Repo);
        var tree = git.PlanAttempt(baseline, Trees, "bench", "worktree");
        var start = Stopwatch.GetTimestamp();
        git.CreateAttempt(tree);
        data.Elapsed("git.worktree", start);
        SqlAndAdmission(store, plan, providers, data);
        await NativeProcess(data, token);
        await Dispatch(store, plan, providers, data, integrate, token);
        RecordRecovery(store, plan, data);
        var before = Events(store);
        data.Record("invariant.lostEvents",
            before.Except(Events(new StateStore(Database))).Count());
    }

    private static JournalEntry[] Events(StateStore store) =>
        store.Plans().SelectMany(p => store.History(p.Id))
            .Concat(store.History(null)).ToArray();

    private void SqlAndAdmission(StateStore store, PlanningService planner,
        ProviderRegistry providers, Measurements data)
    {
        planner.Import("fixture", "sql", 100, [Contract("db")]);
        var attempt = store.Reserve("sql", "db", "one")!;
        var start = Stopwatch.GetTimestamp();
        attempt = store.Transition(attempt.Id, attempt.Version,
            AttemptState.Running, "benchmark");
        data.Elapsed("database.transition", start);
        store.Transition(attempt.Id, attempt.Version, AttemptState.Cancelled,
            "benchmark complete", 0);

        planner.Import("fixture", "admission", 100, [Contract("sched")]);
        var scheduler = new AdmissionService(store, providers, Capacity);
        using var guard = store.AcquireStartupGuard();
        start = Stopwatch.GetTimestamp();
        var slot = scheduler.ReserveNext("admission", _ => "fixture", 2)
            ?? throw new InvalidDataException("Benchmark admission failed.");
        data.Elapsed("scheduler.decision", start);
        store.Transition(slot.Attempt.Id, slot.Attempt.Version,
            AttemptState.Cancelled, "benchmark complete", 0);
    }

    private async Task NativeProcess(Measurements data, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            data.Missing("process.supervision", "Windows jobs required.");
            return;
        }
        var pressure = HostPressure.Measure();
        if (double.IsFinite(pressure.CpuPercent))
            data.Record("host.cpu", pressure.CpuPercent);
        if (pressure.AvailableMemoryBytes > 0)
            data.Record("host.memoryAvailable", pressure.AvailableMemoryBytes / 1048576d);
        using var owner = new ProcessSupervisor();
        var start = Stopwatch.GetTimestamp();
        var result = await owner.RunAsync(new(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "exit", "0"], Repo, Path.Combine(Logs, "native.out"),
            Path.Combine(Logs, "native.err"), TimeSpan.FromSeconds(20)),
            (_, _) => Task.CompletedTask, token);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode != 0 || !result.TreeStopped || result.TimedOut)
            throw new InvalidDataException("Supervised fixture failed.");
        data.Elapsed("process.supervision", start);
    }

    private void RecordRecovery(StateStore store, PlanningService planner,
        Measurements data)
    {
        planner.Import("fixture", "recovery", 100, [Contract("recover")]);
        var attempt = store.Reserve("recovery", "recover", "interrupted");
        if (attempt == null) throw new InvalidDataException("Recovery fixture was blocked.");
        var start = Stopwatch.GetTimestamp();
        var recovered = new RecoveryService(new StateStore(Database),
            new LocalWorkerInspector(), Logs).Recover("recovery");
        data.Elapsed("recovery", start);
        if (recovered.Single().State != AttemptState.Interrupted)
            throw new InvalidDataException("Interrupted reservation lost.");
    }

    private TaskContract Contract(string id)
    {
        var file = id + ".txt";
        var shell = Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        return new(id, "Update fixture", [], [file], [], [], ["check value"],
            [new(shell, ["-NoProfile", "-Command",
            $"if ((Get-Content -Raw {file}) -ne 'updated') {{exit 1}}"])],
            [file], Risk.Low, 1, 180, 0, "simulated", 30);
    }

    private ProviderRegistry Providers()
    {
        string path = Path.Combine(root, "providers.json");
        File.WriteAllText(path, """
            {"providers":[{"id":"local","endpoint":"http://localhost",
            "maxConcurrentRequests":6,"requestsPerMinute":100,"tokensPerMinute":10000}],
            "models":[{"id":"fixture","providerId":"local","runtimeIds":["simulated"],
            "contextTokens":256,"maxOutputTokens":32}]}
            """);
        return ProviderRegistry.Load(path, ["simulated"]);
    }

    private async Task InitializeGit(CancellationToken token)
    {
        await Git(["init", "--template=", "-b", "main"], token);
        foreach (var id in new[] {"single", "left", "right"})
            await File.WriteAllTextAsync(Path.Combine(Repo, id + ".txt"),
                "initial", token);
        await Git(["add", "."], token);
        await Git(["-c", "user.name=PDA", "-c", "user.email=pda@example.invalid",
            "commit", "--no-gpg-sign", "-m", "benchmark baseline"], token);
    }

    private async Task Git(string[] args, CancellationToken token)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repo, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.Where(k =>
            k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        var empty = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = empty;
        foreach (var arg in new[] {"-c", "core.hooksPath=" + empty}.Concat(args))
            start.ArgumentList.Add(arg);
        using var proc = Process.Start(start)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
            await Task.WhenAll(stdout, stderr);
        }
        finally { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        if (proc.ExitCode != 0) throw new IOException("Fixture Git setup failed.");
    }

    public void Dispose()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
