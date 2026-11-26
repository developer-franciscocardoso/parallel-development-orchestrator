using System.Diagnostics;
using Pda.Execution;

namespace Pda.Execution.Tests;

public sealed class SupervisionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private readonly ProcessSupervisor supervisor = new();

    private ProcessRequest Request(string script, int seconds = 30)
    {
        var exe = Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        return new(exe, ["-NoProfile", "-NonInteractive", "-Command", script],
            root, Path.Combine(root, "out"), Path.Combine(root, "err"),
            TimeSpan.FromSeconds(seconds));
    }

    private static Task Started(SupervisedIdentity id, CancellationToken token)
        => Task.CompletedTask;

    [Fact]
    public async Task StreamsAndExitCodeArePreserved()
    {
        var request = Request("[Console]::Write([Console]::In.ReadToEnd());" +
            "[Console]::Error.Write('error');exit 17") with
            { StandardInput = "literal & input" };
        var result = await supervisor.RunAsync(request, Started);
        Assert.Equal(17, result.ExitCode);
        Assert.True(result.TreeStopped);
        Assert.Equal("literal & input",
            File.ReadAllText(request.StandardOutputPath));
        Assert.Equal("error", File.ReadAllText(request.StandardErrorPath));
    }

    [Fact]
    public async Task ArgumentsRemainLiteral()
    {
        var script = Path.Combine(root, "args.ps1");
        File.WriteAllText(script, "[Console]::Write(($args -join '|'))");
        string[] values = ["", "space value", "quoted\"value", @"trail x\",
            "& echo injected"];
        var request = Request("") with { Arguments =
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                .. values] };
        await supervisor.RunAsync(request, Started);
        Assert.Equal(string.Join("|", values),
            File.ReadAllText(request.StandardOutputPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptionStopsTree(bool timeout)
    {
        using var cancel = new CancellationTokenSource();
        var request = Request("Start-Sleep 60", timeout ? 1 : 30);
        var result = await supervisor.RunAsync(request, (id, _) =>
        {
            if (!timeout) cancel.CancelAfter(300);
            return Task.CompletedTask;
        }, cancel.Token);
        Assert.Equal(timeout, result.TimedOut);
        Assert.Equal(!timeout, result.Cancelled);
        Assert.True(result.TreeStopped);
    }

    [Fact]
    public async Task RefusedPersistenceNeverRunsWorker()
    {
        var request = Request("Set-Content marker ran");
        await Assert.ThrowsAsync<IOException>(() => supervisor.RunAsync(
            request, (_, _) => throw new IOException("persistence")));
        Assert.False(File.Exists(Path.Combine(root, "marker")));
    }

    [Fact]
    public async Task DescendantDiesWhenRootExits()
    {
        var request = Request("$p = Start-Process ping.exe " +
            "-ArgumentList '127.0.0.1 -t' -PassThru -WindowStyle Hidden;" +
            "[Console]::Write($p.Id)");
        var result = await supervisor.RunAsync(request, Started);
        Assert.True(result.TreeStopped);
        int pid = int.Parse(File.ReadAllText(request.StandardOutputPath));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task OutputStorageIsBounded()
    {
        var request = Request("[Console]::Write('X' * 100000)") with
            { MaxLogBytes = 42 };
        var outcome = await supervisor.RunAsync(request, Started);
        Assert.True(outcome.OutputTruncated);
        Assert.Equal(42, new FileInfo(request.StandardOutputPath).Length);
    }

    [Fact]
    public async Task DisposalTerminatesWorker()
    {
        var request = Request("Start-Sleep 60");
        var result = await supervisor.RunAsync(request, (_, _) =>
        {
            supervisor.Dispose();
            return Task.CompletedTask;
        });
        Assert.True(result.Cancelled);
        Assert.True(result.TreeStopped);
    }

    [Fact]
    public async Task IdentityDistinguishesLiveAndStoppedTrees()
    {
        var request = Request("exit 0");
        var result = await supervisor.RunAsync(request, (identity, _) =>
        {
            Assert.Equal(ProcessTreeState.Live,
                ProcessSupervisor.Inspect(identity));
            return Task.CompletedTask;
        });
        Assert.Equal(ProcessTreeState.Stopped,
            ProcessSupervisor.Inspect(result.Identity));
    }

    [Fact]
    public async Task SupervisorCrashKillsWorkerAndDescendant()
    {
        var start = new ProcessStartInfo(CrashProbe.DotnetHost)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { typeof(CrashProbe).Assembly.Location, root }
        };
        using var host = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            var file = Path.Combine(root, "pids");
            while (!File.Exists(file))
            {
                Assert.False(host.HasExited, "Probe exited before launch.");
                await Task.Delay(50, deadline.Token);
            }
            var ids = File.ReadAllText(file).Split(',').Select(int.Parse)
                .ToArray();
            using var parent = Process.GetProcessById(ids[0]);
            using var descendant = Process.GetProcessById(ids[1]);
            host.Kill();
            await Task.WhenAll(host.WaitForExitAsync(deadline.Token),
                parent.WaitForExitAsync(deadline.Token),
                descendant.WaitForExitAsync(deadline.Token));
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task TimeoutStopsSuspendedWorkerDespiteStuckPersistence()
    {
        var pending = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SupervisedIdentity? observed = null;
        var run = supervisor.RunAsync(Request("Set-Content marker ran", 1),
            (identity, _) =>
            {
                observed = identity;
                return pending.Task;
            });
        try
        {
            var outcome = await run.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(outcome.TimedOut);
            Assert.True(outcome.TreeStopped);
            Assert.Equal(observed, outcome.Identity);
            Assert.False(File.Exists(Path.Combine(root, "marker")));
        }
        finally
        {
            pending.SetException(new IOException("late persistence failure"));
            try { await run; } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ReplacementEnvironmentOmitsInheritedSecret()
    {
        string key = "PDA_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "inherited");
        try
        {
            string script = $"[Console]::Write(\"$env:{key}|$env:PDA_VALUE\")";
            var request = Request(script) with
            {
                Environment = new Dictionary<string, string>
                {
                    ["SystemRoot"] = Environment.GetFolderPath(
                        Environment.SpecialFolder.Windows),
                    ["PDA_VALUE"] = "explicit"
                }
            };
            await supervisor.RunAsync(request, Started);
            Assert.Equal("|explicit",
                File.ReadAllText(request.StandardOutputPath));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public async Task MismatchedIdentityCannotReportLive()
    {
        var result = await supervisor.RunAsync(Request("exit 0"), (id, _) =>
        {
            var reused = id with { StartedUtc = id.StartedUtc.AddMinutes(-1) };
            Assert.Equal(ProcessTreeState.Unknown,
                ProcessSupervisor.Inspect(reused));
            var fakeJob = id with { JobName = "arbitrary" };
            Assert.Equal(ProcessTreeState.Unknown,
                ProcessSupervisor.Inspect(fakeJob));
            return Task.CompletedTask;
        });
        Assert.Equal(ProcessTreeState.Stopped,
            ProcessSupervisor.Inspect(result.Identity));
    }

    public void Dispose()
    {
        supervisor.Dispose();
        Directory.Delete(root, true);
    }
}
