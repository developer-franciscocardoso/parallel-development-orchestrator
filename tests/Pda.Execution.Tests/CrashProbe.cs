using System.Diagnostics;
using System.Runtime.InteropServices;
using Pda.Execution;

namespace Pda.Execution.Tests;

// The test harness kills this executable while its worker tree is live.
internal static class CrashProbe
{
    internal static string DotnetHost
    {
        get
        {
            var runtime = new DirectoryInfo(RuntimeEnvironment
                .GetRuntimeDirectory());
            return Path.Combine(runtime.Parent!.Parent!.Parent!.FullName,
                "dotnet.exe");
        }
    }

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var folder]) return 2;
        var exe = Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        const string script = "$p = Start-Process ping.exe " +
            "-ArgumentList '127.0.0.1 -t' -PassThru -WindowStyle Hidden;" +
            "[IO.File]::WriteAllText('pids.tmp', \"$PID,$($p.Id)\");" +
            "Move-Item pids.tmp pids;Start-Sleep 90";
        var req = new ProcessRequest(exe, ["-NoProfile", "-Command", script],
            folder, Path.Combine(folder, "out"), Path.Combine(folder, "err"),
            TimeSpan.FromMinutes(2));
        Directory.SetCurrentDirectory(folder);
        using var supervisor = new ProcessSupervisor();
        await supervisor.RunAsync(req, (_, _) => Task.CompletedTask);
        return 0;
    }
}
