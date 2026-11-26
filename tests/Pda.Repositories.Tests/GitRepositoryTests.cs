using System.Diagnostics;
using System.Text;
using Pda.Repositories;

namespace Pda.Repositories.Tests;

public sealed class GitRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "pda git teste ç " + Guid.NewGuid().ToString("N"));

    private readonly GitRepositoryService service = new();
    private string Source => Path.Combine(root, "repo ç");
    private string Worktrees => Path.Combine(root, "worktrees ç");

    public GitRepositoryTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Worktrees);
        Git(Source, "init", "-b", "main");
        Git(Source, "config", "user.name", "Test");
        Git(Source, "config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(Source, "original ç.txt"), "content");
        Git(Source, "add", "--", ".");
        Git(Source, "commit", "-m", "base");
    }

    [Fact]
    public void PinsBaseAndCreatesAnExternalLinkedWorktree()
    {
        var baseline = service.Register(Source);
        var attempt = service.PlanAttempt(baseline, Worktrees, "TASK-1", "a1");
        var created = service.CreateAttempt(attempt);
        Assert.Equal(baseline.Commit, created.Commit);
        Assert.Equal(attempt.Branch, created.Branch);
        Assert.Equal(baseline.Repository.CommonDirectory,
            created.Repository.CommonDirectory);
        Assert.True(created.IsClean);
        Assert.Contains(service.ListWorktrees(baseline.Repository),
            state => state.Path == attempt.Path);
    }

    [Fact]
    public void RefusesCreationAfterSourceHeadAdvances()
    {
        var baseline = service.Register(Source);
        var attempt = service.PlanAttempt(baseline, Worktrees, "task", "a1");
        File.WriteAllText(Path.Combine(Source, "later"), "added");
        Git(Source, "add", "--", "later");
        Git(Source, "commit", "-m", "later");

        Assert.Throws<GitRepositoryException>(
            () => service.CreateAttempt(attempt));
        Assert.False(Directory.Exists(attempt.Path));
    }

    [Fact]
    public void RefusesDirtyAndNestedSources()
    {
        var child = Directory.CreateDirectory(Path.Combine(Source, "nested"));
        Assert.Throws<GitRepositoryException>(
            () => service.Register(child.FullName));
        File.WriteAllText(Path.Combine(child.FullName, "untracked"), "data");
        Assert.Throws<GitRepositoryException>(() => service.Register(Source));
    }

    [Fact]
    public void RefusesUnsafeRefsAndWorktreePaths()
    {
        var baseline = service.Register(Source);
        Assert.Throws<GitRepositoryException>(
            () => service.PlanAttempt(baseline, Worktrees, "..", "a1"));
        Assert.Throws<GitRepositoryException>(
            () => service.PlanAttempt(baseline, Source, "task", "a1"));
        Assert.Throws<GitRepositoryException>(
            () => service.IsAncestor(baseline.Repository, "--all", "HEAD"));
        var attempt = service.PlanAttempt(baseline, Worktrees, "task", "a1");
        Assert.Throws<GitRepositoryException>(() => service.CreateAttempt(
            attempt with { Path = Path.Combine(Source, "unsafe") }));
    }

    [Fact]
    public void BlocksRepeatedCheckoutAndPreservesDirtyAttempt()
    {
        var baseline = service.Register(Source);
        var attempt = service.PlanAttempt(baseline, Worktrees, "task", "a1");
        service.CreateAttempt(attempt);
        var file = Path.Combine(attempt.Path, "pending");
        File.WriteAllText(file, "preserve");
        Assert.False(service.Inspect(attempt.Path).IsClean);
        Assert.Throws<GitRepositoryException>(
            () => service.CreateAttempt(attempt));
        Assert.Equal("preserve", File.ReadAllText(file));
    }

    [Fact]
    public void ReadsBothRenamePathsAsUtf8AndChecksAncestry()
    {
        var baseline = service.Register(Source);
        var attempt = service.PlanAttempt(baseline, Worktrees, "task", "a1");
        service.CreateAttempt(attempt);
        var renamed = OperatingSystem.IsWindows()
            ? "renamed ç #.txt" : "renamed ç \t.txt";
        Git(attempt.Path, "mv", "--", "original ç.txt", renamed);
        Git(attempt.Path, "commit", "-m", "rename");
        var head = service.Inspect(attempt.Path).Commit;

        var change = Assert.Single(service.ChangedPaths(
            baseline.Repository, baseline.Commit, head));
        Assert.Equal(new GitPathChange("R100", "original ç.txt", renamed),
            change);
        Assert.True(service.IsAncestor(baseline.Repository, baseline.Commit,
            head));
        Assert.False(service.IsAncestor(baseline.Repository, head,
            baseline.Commit));
    }

    [Fact]
    public void CheckoutHookDoesNotExecute()
    {
        var baseline = service.Register(Source);
        var hooks = Directory.CreateDirectory(Path.Combine(root, "hooks"));
        var hook = Path.Combine(hooks.FullName, "post-checkout");
        File.WriteAllText(hook, "#!/bin/sh\necho ran > hook-ran\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Git(Source, "config", "core.hooksPath", hooks.FullName);

        var attempt = service.PlanAttempt(baseline, Worktrees, "task", "a1");
        service.CreateAttempt(attempt);
        Assert.False(File.Exists(Path.Combine(attempt.Path, "hook-ran")));
    }

    [Fact]
    public void CreatesIndependentAttemptsUnderOneExternalRoot()
    {
        var baseline = service.Register(Source);
        var first = service.PlanAttempt(baseline, Worktrees, "a-b", "c");
        service.CreateAttempt(first);
        var next = service.PlanAttempt(baseline, Worktrees, "a", "b-c");
        service.CreateAttempt(next);
        Assert.NotEqual(first.Path, next.Path);
        Assert.NotEqual(first.Branch, next.Branch);
    }

    [Fact]
    public void RefusesLocalFilterDriversBeforeCheckout()
    {
        var baseline = service.Register(Source);
        Git(Source, "config", "filter.untrusted.smudge", "unsafe-command");
        Assert.Throws<GitRepositoryException>(() => service.PlanAttempt(
            baseline, Worktrees, "task", "a1"));
    }

    [Fact]
    public void RejectsDirectoryLinksInSourcesAndDestinations()
    {
        var baseline = service.Register(Source);
        var linkedSource = Path.Combine(root, "source alias");
        var linkedDestination = Path.Combine(root, "destination alias");
        CreateDirectoryLink(linkedSource, Source);
        CreateDirectoryLink(linkedDestination, Worktrees);

        try
        {
            Assert.Throws<GitRepositoryException>(
                () => service.Register(linkedSource));
            Assert.Throws<GitRepositoryException>(() => service.PlanAttempt(
                baseline, linkedDestination, "task", "alias"));
        }
        finally
        {
            Directory.Delete(linkedSource);
            Directory.Delete(linkedDestination);
        }
    }

    [Fact]
    public void RefusesSourceMadeDirtyAfterPlanning()
    {
        var baseline = service.Register(Source);
        var attempt = service.PlanAttempt(baseline, Worktrees, "task", "a1");
        var path = Path.Combine(Source, "original ç.txt");
        File.WriteAllText(path, "local edits");

        Assert.Throws<GitRepositoryException>(
            () => service.CreateAttempt(attempt));
        Assert.Equal("local edits", File.ReadAllText(path));
        Assert.False(Directory.Exists(attempt.Path));
    }

    [Fact]
    public void RejectsGitEnvironmentOverrides()
    {
        var key = "GIT_INDEX_FILE";
        var original = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, Path.Combine(root, "absent"));
        try
        {
            Assert.True(service.Inspect(Source).IsClean);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*",
                     SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // Test fixtures use junctions to avoid requiring Administrator rights.
        var shell = Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = "$ErrorActionPreference = 'Stop'\n" +
            $"New-Item -ItemType Junction -Path '{Quote(link)}' " +
            $"-Target '{Quote(target)}' | Out-Null";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static string Quote(string path) => path.Replace("'", "''");

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000));
        Assert.True(process.ExitCode == 0, output.Result + error.Result);
    }
}
