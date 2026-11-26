using System.Diagnostics;
using Pda.Execution;
using Pda.Repositories;

namespace Pda.Recovery.Tests;

public sealed class LocalInspectorTests
{
    [Fact]
    public async Task RealJobAndGitIdentitySurviveWorkerCompletion()
    {
        var directory = Directory.CreateTempSubdirectory("pda-inspect-");
        try
        {
            var source = Directory.CreateDirectory(
                Path.Combine(directory.FullName, "source")).FullName;
            var worktrees = Directory.CreateDirectory(
                Path.Combine(directory.FullName, "workers")).FullName;
            Git(source, "init", "-b", "main");
            Git(source, "-c", "user.name=Test", "-c", "user.email=test@local",
                "commit", "--allow-empty", "-m", "initial");
            var git = new GitRepositoryService();
            var baseline = git.Register(source);
            var attempt = git.PlanAttempt(baseline, worktrees, "task", "one");
            git.CreateAttempt(attempt);
            var root = attempt.Path;
            var inspector = new LocalWorkerInspector();
            WorkerRecord? record = null;
            using var supervisor = new ProcessSupervisor();
            var exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            await supervisor.RunAsync(new(exe, ["/d", "/c", "exit 0"],
                root, Path.Combine(root, "out"), Path.Combine(root, "err"),
                TimeSpan.FromSeconds(20)), (identity, _) =>
            {
                record = new("attempt", root, attempt.Branch,
                    baseline.Commit, identity.ProcessId, identity.StartedUtc,
                    identity.JobName, Path.Combine(root, "out"),
                    Path.Combine(root, "err"),
                    baseline.Repository.CommonDirectory);
                var observation = inspector.Inspect(record);
                Assert.Equal(TreeCondition.Live, observation.Tree);
                Assert.True(observation.RepositoryIntact);
                return Task.CompletedTask;
            });
            Assert.Equal(TreeCondition.Stopped, inspector.Inspect(record!).Tree);
            Assert.False(inspector.Inspect(record! with
                { Branch = "different" }).RepositoryIntact);
            Assert.True(File.Exists(Path.Combine(root, "out")));
            var preserved = Path.Combine(directory.FullName, "preserved");
            Directory.Move(root, preserved);
            Git(source, "clone", "--no-hardlinks", "--branch", attempt.Branch,
                source, root);
            Assert.False(inspector.Inspect(record!).RepositoryIntact);
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles("*",
                SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            directory.Delete(true);
        }
    }

    private static void Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
