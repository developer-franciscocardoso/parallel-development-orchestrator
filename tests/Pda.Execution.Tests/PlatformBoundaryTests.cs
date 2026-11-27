using Pda.Execution;

namespace Pda.Execution.Tests;

public sealed class PlatformBoundaryTests
{
    [Fact]
    public async Task NativeSupportBoundaryPrecedesWorkerSideEffects()
    {
        var dir = Directory.CreateTempSubdirectory();
        var windows = OperatingSystem.IsWindows();
        var stdout = Path.Combine(dir.FullName, "out");
        var stderr = Path.Combine(dir.FullName, "err");
        var request = new ProcessRequest(windows ?
            Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe") : "/bin/true",
            windows ? ["-NoProfile", "-NonInteractive", "-Command", "exit 0"]
                : [], dir.FullName, stdout,
            stderr, TimeSpan.FromSeconds(20));
        using var supervisor = new ProcessSupervisor();
        var observed = false;
        Task<ProcessOutcome> Start() => supervisor.RunAsync(request, (_, _) =>
        {
            observed = true;
            return Task.CompletedTask;
        });
        try
        {
            if (windows)
            {
                var completed = await Start();
                Assert.True(observed);
                Assert.True(completed.TreeStopped);
                Assert.Equal(0, completed.ExitCode);
                return;
            }
            await Assert.ThrowsAsync<PlatformNotSupportedException>(Start);
            Assert.False(observed);
            Assert.False(File.Exists(stdout));
            Assert.False(File.Exists(stderr));
        }
        finally { dir.Delete(true); }
    }
}
