using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Pda.Agents;
using Pda.Core;
using Pda.Execution;
using Pda.Repositories;
using Pda.Security;

namespace Pda.Verification;

public sealed class SandboxedVerificationRunner(CodexProfile profile)
    : IVerificationRunner
{
    public async Task<CheckReport> RunAsync(VerificationInput input,
        CancellationToken token)
    {
        var checks = new List<CommandEvidence>();
        CheckReport End(bool passed, string detail) =>
            new(input.Binding, passed, checks.AsReadOnly(), detail);
        try
        {
            token.ThrowIfCancellationRequested();
            if (input.Contract.Verification.Length is 0 or > 16)
                return End(false, "A bounded verification command is required.");
            Checkout.Changes(input);
            Checkout.Fresh(input);
            var logs = TrustedPaths.Directory(input.LogDirectory);
            TrustedPaths.OutsideRepository(logs);
            foreach (var path in (profile.ToolDirectories ?? []).Concat(
                profile.ReadOnlyDirectories ?? []).Append(profile.Home)
                .Append(Path.GetDirectoryName(profile.Executable)!))
                TrustedPaths.Disjoint(Path.GetFullPath(path), logs);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            var limit = TimeSpan.FromSeconds(Math.Min(input.Contract.TimeoutSeconds,
                86_400));
            if (limit <= TimeSpan.Zero) return End(false, "Invalid time limit.");
            deadline.CancelAfter(limit);
            if (!await CodexSandboxProbe.VerifyAsync(profile, input.Worktree,
                deadline.Token)) return End(false, "MXC confinement probe failed.");
            foreach (var command in input.Contract.Verification)
            {
                var check = await Execute(input.Worktree, logs, command, limit,
                    deadline.Token);
                checks.Add(check);
                if (check.TimedOut || check.Cancelled || check.Truncated ||
                    check.ExitCode != 0) return End(false, "Command failed.");
                Checkout.Verify(input);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return End(true, "All declared commands passed inside MXC.");
        }
        catch (OperationCanceledException)
        {
            return End(false, token.IsCancellationRequested
                ? "Verification cancelled." : "Verification deadline expired.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or
            UnauthorizedAccessException or GitRepositoryException or
            ArgumentException or Win32Exception or PlatformNotSupportedException)
        {
            return End(false, "Verification unavailable: " + e.Message);
        }
    }

    private async Task<CommandEvidence> Execute(string tree, string logs,
        VerificationCommand command, TimeSpan timeout, CancellationToken token)
    {
        var environment = CodexPolicy.EnvironmentFor(profile, tree);
        var options = CodexPolicy.WorkerOptions(profile, tree);
        var id = Guid.NewGuid().ToString("N");
        var output = TrustedPaths.FreshFile(Path.Combine(logs, id + ".out"), tree);
        var error = TrustedPaths.FreshFile(Path.Combine(logs, id + ".err"), tree);
        var request = new ProcessRequest(profile.Executable,
            [..options, "sandbox", "--include-managed-config",
            "--permission-profile", "pda", "--cd", tree, "--",
            command.Executable, ..command.Arguments], tree, output, error,
            timeout, MaxLogBytes: 1024 * 1024, Environment: environment);
        using var supervisor = new ProcessSupervisor();
        var started = Stopwatch.GetTimestamp();
        var result = await supervisor.RunAsync(request,
            (_, _) => Task.CompletedTask, token);
        return new(command, result.ExitCode,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            output, error, Digest(output), Digest(error), result.TimedOut,
            result.Cancelled || !result.TreeStopped,
            result.OutputTruncated || result.ErrorTruncated);
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
