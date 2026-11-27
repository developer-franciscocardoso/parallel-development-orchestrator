using System.Diagnostics;
using System.Security.Cryptography;
using Pda.Agents;
using Pda.Core;
using Pda.Persistence;
using Pda.Repositories;
using Pda.Scheduling;
using Pda.Verification;

namespace Pda.Quality.Tests;

public sealed class QualityTests : IDisposable, IVerificationRunner,
    ICandidateReviewer
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "pda-quality-" + Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService git = new();
    private readonly StateStore store;
    private readonly PlanWorktree plan;
    private readonly GitCandidate candidate;
    private readonly Attempt attempt;
    private readonly string evidence;
    private readonly List<string> checkTrees = [];
    private bool rejectReview;
    private bool failRegression;
    private bool crashAfterAdvance;

    private readonly TaskContract contract = new("t", "Make a change", [],
        ["src"], [], [], ["fixture works"], [new("fixture-test", [])],
        ["patched source"], Risk.Low, 1, 60, 0, "simulated", 30);

    private sealed record Profile(string Commit) : IProjectProfile;

    public QualityTests()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        var trees = Directory.CreateDirectory(Path.Combine(root, "trees")).FullName;
        evidence = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
        Git(repo, "init", "-b", "main");
        Git(repo, "commit", "--allow-empty", "-m", "baseline");
        var origin = git.Register(repo);
        store = new StateStore(Path.Combine(root, "state.db"));
        store.RegisterProject("project", repo);
        store.SaveProjectProfile("project", new Profile(origin.Commit), 0);
        store.CreatePlan("project", repo, "plan", [contract], 50, 1);
        plan = git.PlanWorkspace(origin, trees, "plan");
        var baseline = git.EnsureWorkspace(plan, origin.Commit);
        store.PutArtifact("plan", "workspace", "plan", plan);
        attempt = store.Reserve("plan", contract.Id, "request")!;
        var worker = git.PlanAttempt(baseline, trees, contract.Id, attempt.Id);
        git.CreateAttempt(worker);
        var src = Directory.CreateDirectory(Path.Combine(worker.Path, "src"));
        File.WriteAllText(Path.Combine(src.FullName, "feature"), "changed");
        Git(worker.Path, "add", "src/feature");
        Git(worker.Path, "commit", "-m", "candidate");
        var head = git.Register(worker.Path);
        candidate = new(worker, head.Commit, git.ChangedPaths(head.Repository,
            origin.Commit, head.Commit));
        store.PutArtifact("plan", "dispatch", attempt.Id, new DispatchRecord(
            worker, "out", "err", "report", "simulated", null));
        store.PutArtifact("plan", "result", attempt.Id, new RuntimeResult(
            RuntimeError.None, "simulated", new(10, 0, 3), new(contract.Id,
                attempt.Id, "completed", "fixture", head.Commit, [], [], []),
            null, true));
        attempt = store.Transition(attempt.Id, attempt.Version,
            AttemptState.Running, "fixture");
        attempt = store.Transition(attempt.Id, attempt.Version,
            AttemptState.AwaitingVerification, "fixture ended", 13);
    }

    [Fact]
    public async Task CrashAfterRefAdvanceResumesWithoutRerunningChecks()
    {
        crashAfterAdvance = true;
        await Assert.ThrowsAsync<IOException>(() => ProcessAttempt());
        Assert.Equal(AttemptState.Verified, store.Attempts("plan").Single().State);
        var integrated = await ProcessAttempt();
        Assert.Equal(AttemptState.Integrated, integrated.State);
        Assert.Equal(3, checkTrees.Distinct().Count());
        Assert.DoesNotContain(candidate.Attempt.Path, checkTrees);
        Assert.Equal(plan.Baseline.Commit, git.Register(plan.Baseline.Repository.Root).Commit);
        Assert.Single(store.Artifacts<IntegratedChange>("plan", "integration"));
        Assert.Equal(integrated, await ProcessAttempt());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectionsPreserveOriginalPlanHead(bool review, bool merge)
    {
        rejectReview = review;
        failRegression = merge;
        Assert.Equal(AttemptState.Blocked, (await ProcessAttempt()).State);
        Assert.Equal(plan.Baseline.Commit, git.Register(plan.Path).Commit);
        Assert.Empty(store.Artifacts<IntegratedChange>("plan", "integration"));
        Assert.Single(store.Artifacts<QualityFailure>("plan", "quality-failure"));
    }

    private Task<Attempt> ProcessAttempt()
    {
        var ops = new GitQualityOperations(git.CaptureCandidate,
            git.PrepareMerge, Advance);
        return new QualityCoordinator(store, ops, this, this, evidence)
            .ProcessAsync("plan", attempt.Id);
    }

    private RepositoryBaseline Advance(GitMerge merge)
    {
        var result = git.AdvancePlan(merge);
        if (crashAfterAdvance)
        {
            crashAfterAdvance = false;
            throw new IOException("Injected post-CAS failure");
        }
        return result;
    }

    public Task<CheckReport> RunAsync(VerificationInput input, CancellationToken token)
    {
        checkTrees.Add(input.Worktree);
        bool pass = !failRegression || checkTrees.Count == 1;
        string output = Path.Combine(input.LogDirectory, "stdout");
        string error = Path.Combine(input.LogDirectory, "stderr");
        File.WriteAllText(output, "fixture");
        File.WriteAllText(error, "");
        CommandEvidence evidence = new(input.Contract.Verification.Single(),
            pass ? 0 : 1, 1, output, error, Hash(output), Hash(error), false,
            false, false);
        return Task.FromResult(new CheckReport(input.Binding, pass, [evidence],
            "Synthetic command results"));
    }

    private static string Hash(string file) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    public Task<ReviewReport> ReviewAsync(VerificationInput input,
        CancellationToken token)
    {
        checkTrees.Add(input.Worktree);
        return Task.FromResult(new ReviewReport(input.Binding, !rejectReview,
            "Synthetic reviewer", []));
    }

    private static void Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir };
        foreach (var arg in new[] {"-c", "user.name=PDA", "-c",
            "user.email=pda@invalid", "-c", "commit.gpgsign=false"}.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(root, "*",
                     SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
