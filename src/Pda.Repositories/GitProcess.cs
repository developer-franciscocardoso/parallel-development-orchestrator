using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Pda.Repositories;

internal sealed class GitProcess(string executable)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly string[] SafeConfig =
    [
        "core.fsmonitor=false",
        "core.ignoreStat=false",
        "core.checkStat=default",
        "core.trustctime=true",
        "core.sparseCheckout=false",
        "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        "core.symlinks=false",
        "core.quotePath=false",
        "core.untrackedCache=false",
        "submodule.recurse=false",
        "fetch.recurseSubmodules=false",
        "protocol.allow=never",
        "gc.auto=0",
        "maintenance.auto=false"
    ];

    public static string FindExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new GitRepositoryException("Git is unavailable on PATH.");
    }

    public string Require(string root, params string[] args)
    {
        var result = Run(root, args);
        if (result.ExitCode != 0)
            throw Error(args[0], result);
        return result.Output;
    }

    public static GitRepositoryException Error(string command, GitResult result)
        => new($"Git {command} failed (exit {result.ExitCode}).");

    public GitResult Run(string root, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        }

        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_GLOBAL"] = nullDevice;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        start.ArgumentList.Add("--no-pager");
        foreach (var config in SafeConfig)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(config);
        }
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        try
        {
            return Execute(start, args[0]).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is Win32Exception or IOException
            or DecoderFallbackException or OperationCanceledException)
        {
            throw new GitRepositoryException(
                $"Could not complete Git {args[0]}.", error);
        }
    }

    private static async Task<GitResult> Execute(ProcessStartInfo start,
        string command)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(60));
        using var process = Process.Start(start)
            ?? throw new GitRepositoryException("Could not start Git.");
        process.StandardInput.Close();
        var output = ReadOutput(process.StandardOutput.BaseStream,
            deadline.Token);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null,
            deadline.Token);
        try
        {
            var result = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token)
                .ConfigureAwait(false);
            return new GitResult(process.ExitCode, result);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            if (deadline.IsCancellationRequested)
                throw new GitRepositoryException($"Git {command} timed out.");
            throw;
        }
    }

    private static async Task<string> ReadOutput(Stream stream,
        CancellationToken cancellationToken)
    {
        const int maxBytes = 32 * 1024 * 1024;
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                return Utf8.GetString(output.GetBuffer(), 0,
                    (int)output.Length);

            if (output.Length + count > maxBytes)
                throw new GitRepositoryException("Git output exceeds 32 MiB.");

            output.Write(buffer, 0, count);
        }
    }
}

internal sealed record GitResult(int ExitCode, string Output);
