using System.Net;
using System.Net.Sockets;
using System.Text;
using Pda.Execution;
using Pda.Security;

namespace Pda.Agents;

internal sealed class CodexProbe(CodexProfile profile, string tree,
    IReadOnlyDictionary<string, string> environment, ProcessSupervisor owner)
{
    internal async Task<(int Exit, string Text)> Read(string[] args,
        CancellationToken token, bool includeOutput = true)
    {
        string prefix = Path.Combine(profile.Home, "tmp",
            "diagnostic-" + Guid.NewGuid().ToString("N"));
        string output = prefix + ".out", error = prefix + ".err";
        var req = new ProcessRequest(profile.Executable, args, tree, output,
            error, TimeSpan.FromSeconds(20), Environment: environment);
        try
        {
            var result = await owner.RunAsync(req, (_, _) => Task.CompletedTask,
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Cancelled || result.TimedOut || result.OutputTruncated)
                throw new IOException("Incomplete runtime diagnostic.");
            string text = includeOutput
                ? await File.ReadAllTextAsync(output, token) : "";
            return (result.ExitCode, text);
        }
        finally
        {
            File.Delete(output);
            File.Delete(error);
        }
    }

    internal async Task<bool> Sandbox(string[] options, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var control = new TcpClient())
        {
            await control.ConnectAsync(IPAddress.Loopback, port, token);
            using var accepted = await listener.AcceptTcpClientAsync(token);
        }
        string id = Guid.NewGuid().ToString("N");
        string inside = Path.Combine(tree, ".pda-" + id);
        string outside = Path.Combine(profile.Home, "fence-" + id);
        using (var file = File.Open(outside, FileMode.CreateNew))
            file.WriteByte(42);
        var git = Path.Combine(tree, ".git");
        var gitProbe = Directory.Exists(git)
            ? Path.Combine(git, "pda-probe-" + id) : git;
        bool reserved = false;
        try
        {
            using (File.Open(inside, FileMode.CreateNew)) reserved = true;
            var script = CanaryScript(inside, outside, gitProbe, port);
            string command = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(script));
            var powershell = Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");
            var result = await Read([..options, "sandbox",
                "--include-managed-config", "--permission-profile", "pda",
                "--cd", tree, "--", powershell, "-NoProfile", "-NonInteractive",
                "-EncodedCommand", command], token, false);
            return result.Exit == 0 && !listener.Pending()
                && File.ReadAllText(inside) == "inside"
                && File.ReadAllBytes(outside).SequenceEqual(new byte[] {42});
        }
        finally
        {
            if (gitProbe != git) File.Delete(gitProbe);
            if (reserved) File.Delete(inside);
            File.Delete(outside);
        }
    }

    private static string CanaryScript(string inside, string outside,
        string gitProbe, int port)
    {
        static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
        // Native process receives only these fixed diagnostic operations.
        return $$"""
            $ErrorActionPreference = 'Stop'
            [IO.File]::WriteAllText({{Literal(inside)}}, 'inside')
            try { [void][IO.File]::ReadAllBytes({{Literal(outside)}}); exit 11 }
            catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
            try { [IO.File]::WriteAllText({{Literal(outside)}}, 'bad'); exit 12 }
            catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
            try {
                $f = [IO.File]::Open({{Literal(gitProbe)}}, 'OpenOrCreate', 'Write')
                $f.Dispose()
                exit 14
            } catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
            $tcp = [Net.Sockets.TcpClient]::new()
            try {
                if ($tcp.ConnectAsync('127.0.0.1', {{port}}).Wait(1500)) { exit 13 }
            } catch { } finally { $tcp.Dispose() }
            exit 0
            """;
    }
}
