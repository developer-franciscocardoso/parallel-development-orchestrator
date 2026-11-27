using System.Diagnostics;
using System.Security.Cryptography;
using Pda.Core;
using Pda.Repositories;
using Pda.Security;

namespace Pda.Verification.Tests;

public sealed class NativeMxcFactAttribute : FactAttribute
{
    internal static string? Executable()
    {
        var configured = Environment.GetEnvironmentVariable("PDA_CODEX_EXE");
        if (configured != null) return File.Exists(configured) ? configured : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(p => Path.Combine(p, "codex.exe")).FirstOrDefault(File.Exists);
    }

    public NativeMxcFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Executable() == null)
            Skip = "Requires native Codex with Windows MXC.";
    }
}

public sealed class VerificationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private string Tree => Path.Combine(root, "tree");
    private string Logs => Path.Combine(root, "logs");
    private readonly string baseline;

    public VerificationTests()
    {
        Directory.CreateDirectory(Tree);
        Directory.CreateDirectory(Logs);
        Git("init", "-q");
        Git("config", "user.name", "fixture");
        Git("config", "user.email", "fixture@example.test");
        File.WriteAllText(Path.Combine(Tree, ".gitignore"), "scratch.tmp\n");
        File.WriteAllText(Path.Combine(Tree, "item.txt"), "original\n");
        Git("add", ".");
        Git("commit", "-qm", "base");
        baseline = Git("rev-parse", "HEAD");
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Tree, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return text.Trim();
    }

    private VerificationInput Candidate(string content = "verified\n",
        string script = "exit 0", Risk risk = Risk.Low)
    {
        File.WriteAllText(Path.Combine(Tree, "item.txt"), content);
        Git("add", ".");
        Git("commit", "-qm", "candidate");
        var git = new GitRepositoryService();
        var head = git.Inspect(Tree);
        var commands = new[] {new VerificationCommand(
            Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", script])};
        var task = new TaskContract("t", "check", [], ["item.txt"], [], [],
            ["pass the declared check"], commands, ["edited item"], risk,
            1, 90, 0, "codex", 100);
        return new(CandidateBinding.For(baseline, head.Commit, task),
            task, Tree, Logs, git.ChangedPaths(head.Repository, baseline,
                head.Commit));
    }

    private SandboxedVerificationRunner Runner()
    {
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        return new(new CodexProfile(NativeMxcFactAttribute.Executable()!, home));
    }

    [Fact]
    public async Task CleanCandidatePassesMechanicalChecks()
    {
        var input = Candidate();
        var report = await new MechanicalReviewer().ReviewAsync(input, default);
        Assert.True(report.Approved, string.Join(" ", report.Findings));
        Assert.Equal(input.Binding, report.Binding);
        Assert.StartsWith("pda.mechanical", report.Reviewer);
    }

    [Theory]
    [InlineData("<<<<<<< branch\n", Risk.Low, "merge marker")]
    [InlineData("-----BEGIN PRIVATE KEY-----", Risk.Low, "key material")]
    [InlineData("new", Risk.High, "specialist")]
    public async Task EscalatesUnreviewableChanges(string contents, Risk risk,
        string reason)
    {
        var report = await new MechanicalReviewer().ReviewAsync(
            Candidate(contents, risk: risk), default);
        Assert.False(report.Approved);
        Assert.Contains(report.Findings, f => f.Impact.Contains(reason));
    }

    [Fact]
    public async Task RejectsOutOfScopeAndForgedChangeLists()
    {
        var input = Candidate();
        var reviewer = new MechanicalReviewer();
        var task = input.Contract with {Owned = ["other.txt"]};
        var invalid = input with {Contract = task, Binding =
            CandidateBinding.For(baseline, input.Binding.CandidateCommit, task)};
        Assert.False((await reviewer.ReviewAsync(invalid, default)).Approved);
        Assert.False((await reviewer.ReviewAsync(input with {Changes = []},
            default)).Approved);
        Assert.False((await reviewer.ReviewAsync(input with {Contract = task},
            default)).Approved);
    }

    [Fact]
    public async Task RejectsIgnoredWorkerOutputAndModifiedCandidate()
    {
        var input = Candidate();
        var path = Path.Combine(Tree, "scratch.tmp");
        File.WriteAllText(path, "poisoned");
        var report = await new MechanicalReviewer().ReviewAsync(input, default);
        Assert.False(report.Approved);
        File.Delete(path);
        File.WriteAllText(Path.Combine(Tree, "item.txt"), "modified");
        Assert.False((await new MechanicalReviewer().ReviewAsync(input, default))
            .Approved);
    }

    [NativeMxcFact]
    public async Task RealMxcChecksCandidateAndHashesLogs()
    {
        var input = Candidate(script:
            "if ((Get-Content item.txt) -ne 'verified') { exit 9 }; 'checked'");
        var report = await Runner().RunAsync(input, default);
        Assert.True(report.Passed, report.Detail + " " +
            string.Join(" ", report.Checks.Select(c => File.ReadAllText(c.ErrorLog))));
        var check = Assert.Single(report.Checks);
        Assert.Contains("checked", File.ReadAllText(check.OutputLog));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(check.OutputLog))), check.OutputSha256);
    }

    [NativeMxcFact]
    public async Task RealMxcFailureRetainsLogsAndBlocksTrackedMutations()
    {
        var input = Candidate(script: "exit 7");
        var runner = Runner();
        var report = await runner.RunAsync(input, default);
        Assert.False(report.Passed);
        Assert.NotEqual(0, Assert.Single(report.Checks).ExitCode);
        var task = input.Contract with
        {
            Verification = [input.Contract.Verification[0] with
                { Arguments = ["-NoProfile", "-Command", "Set-Content item.txt changed"] }]
        };
        var mutation = input with {Contract = task, Binding =
            CandidateBinding.For(baseline, input.Binding.CandidateCommit, task)};
        report = await runner.RunAsync(mutation, default);
        Assert.False(report.Passed);
        Assert.Equal(0, Assert.Single(report.Checks).ExitCode);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*",
            SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
