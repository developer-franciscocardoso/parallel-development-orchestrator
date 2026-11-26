using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Pda.Execution;
using Pda.Security;

namespace Pda.Agents;

public sealed class CodexRuntime(CodexProfile profile,
    IManagedPolicyVerifier? additionalPolicy = null) : IAgentRuntime
{
    public string Id => "codex";

    public async Task<RuntimeDiscovery> DiscoverAsync(AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        string? version = null;
        bool authenticated = false;
        List<string> capabilities = [];
        RuntimeDiscovery End(RuntimeError error, string detail) =>
            new(Id, version, authenticated, capabilities.AsReadOnly(),
                error, detail);
        try
        {
            if (!OperatingSystem.IsWindows())
                return End(RuntimeError.Unavailable, "Windows MXC required.");
            Requests.Validate(request);
            var env = CodexPolicy.EnvironmentFor(profile, request.Worktree);
            Requests.CheckConfig(request.Worktree, profile.Home);
            Requests.CheckEvidence(request, profile);
            using var owner = new ProcessSupervisor();
            var probe = new CodexProbe(profile, request.Worktree, env, owner);
            async Task<string> Required(string[] args)
            {
                var data = await probe.Read(args, cancellationToken);
                if (data.Exit != 0)
                    throw new InvalidDataException("CLI diagnostic failed: "
                        + string.Join(" ", args.Where(s => !s.Contains('=')
                            && s != "-c").TakeLast(3)));
                return data.Text;
            }
            version = (await Required(["--version"])).Trim();
            if (!version.StartsWith("codex-cli ", StringComparison.Ordinal)
                || version.Length > 120 || version.Any(char.IsControl))
                return End(RuntimeError.Unavailable, "Unrecognized CLI version.");
            string help = await Required(["exec", "--help"]);
            string[] switches = ["--json", "--output-schema", "--ignore-rules",
                "--ignore-user-config", "--ephemeral", "--strict-config",
                "--output-last-message"];
            if (switches.Any(s => !help.Contains(s, StringComparison.Ordinal)))
                return End(RuntimeError.Unavailable, "Missing exec capabilities.");
            capabilities.AddRange(["events", "reports", "usage"]);
            var opts = CodexPolicy.WorkerOptions(profile, request.Worktree)
                .ToArray();
            string startup = await Required([..opts, "app-server",
                "--strict-config", "--print-startup-config"]);
            bool managed = ManagedRequirements.Inspect(startup);
            string features = await Required([..opts, "features", "list"]);
            if (!CodexPolicy.DisabledFeaturesObserved(features))
                return End(RuntimeError.PolicyDenied,
                    "Effective feature restrictions could not be verified.");
            string mcp = await Required([..opts, "mcp", "list", "--json"]);
            string plugins = await Required([..opts, "plugin", "list", "--json"]);
            if (!CodexPolicy.EmptyCatalogs(mcp, plugins))
                return End(RuntimeError.PolicyDenied, "External tools configured.");
            capabilities.Add("restricted-tools");
            if (!await probe.Sandbox(opts, cancellationToken))
                return End(RuntimeError.PolicyDenied, "MXC confinement probe failed.");
            capabilities.Add("mxc");
            var args = CodexLaunch.Arguments(request, opts);
            if (additionalPolicy != null)
            {
                using var exe = File.OpenRead(profile.Executable);
                var hash = await SHA256.HashDataAsync(exe, cancellationToken);
                var context = new PolicyContext(profile.Executable,
                    Convert.ToHexString(hash), version, profile.Home,
                    request.Worktree, args, env, managed);
                if (!await additionalPolicy.VerifyAsync(context, cancellationToken))
                    return End(RuntimeError.PolicyDenied, "Deployment policy denied.");
            }
            var login = await probe.Read([..opts, "login", "status"],
                cancellationToken, includeOutput: false);
            authenticated = login.Exit == 0;
            return authenticated
                ? End(RuntimeError.None, "Runtime and policy checks passed.")
                : End(RuntimeError.Authentication,
                    "Isolated runtime profile requires authentication.");
        }
        catch (FileNotFoundException)
        { return End(RuntimeError.Unavailable, "Trusted runtime file missing."); }
        catch (Exception e) when (Expected(e))
        {
            return End(RuntimeError.PolicyDenied, e is InvalidDataException
                ? e.Message : "Runtime capability inspection failed.");
        }
    }

    public async Task<RuntimeResult> RunAsync(AgentRequest request,
        AgentCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(request.Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        try
        {
            var state = await DiscoverAsync(request, stop.Token);
            if (!state.Ready)
                return new(state.Error, state.Detail, null, null, null, false);
            var env = CodexPolicy.EnvironmentFor(profile, request.Worktree);
            Requests.CheckConfig(request.Worktree, profile.Home);
            Requests.CheckEvidence(request, profile);
            var worker = CodexLaunch.Process(profile, request, env);
            var result = await Execute(request, worker, callbacks, stop.Token);
            return result.Error == RuntimeError.Cancelled
                && deadline.IsCancellationRequested
                ? result with { Error = RuntimeError.Timeout } : result;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return new(deadline.IsCancellationRequested ? RuntimeError.Timeout
                : RuntimeError.Cancelled, "Runtime interrupted.", null, null,
                null, false);
        }
        catch (Exception e) when (Expected(e))
        {
            return new(RuntimeError.Failed, "Runtime operation failed.",
                null, null, null, false);
        }
        finally { stop.Cancel(); }
    }

    internal static async Task<RuntimeResult> Execute(AgentRequest request,
        ProcessRequest worker, AgentCallbacks callbacks, CancellationToken token)
    {
        using var owner = new ProcessSupervisor();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var tail = new EventTail();
        // Reserve the external report path before passing it to the CLI.
        using (File.Open(request.ReportPath, FileMode.CreateNew)) { }
        var run = owner.RunAsync(worker, callbacks.Started, stop.Token);
        ProcessOutcome outcome;
        try { outcome = await tail.Follow(worker.StandardOutputPath, run,
            callbacks.Event); }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(false);
        }
        var parser = tail.Parser;
        RuntimeResult End(RuntimeError error, string detail,
            TokenUsage? usage = null, WorkerReport? report = null)
            => new(error, detail, usage, report, outcome, false);
        if (outcome.Cancelled || outcome.TimedOut)
            return End(outcome.TimedOut ? RuntimeError.Timeout
                : RuntimeError.Cancelled, "Worker interrupted.");
        if (outcome.ExitCode != 0 || parser.FailedTurn)
            return End(RuntimeError.Failed, "Worker or turn failed.");
        if (parser.Usage == null)
            return End(RuntimeError.Protocol, "Incomplete usage evidence.");
        try
        {
            var report = WorkerReport.Read(request);
            return End(report.Status == "completed" ? RuntimeError.None
                : RuntimeError.Failed, "Worker report received.",
                parser.Usage, report);
        }
        catch (Exception e) when (Expected(e))
        { return End(RuntimeError.InvalidReport, "Invalid worker report.", parser.Usage); }
    }

    private static bool Expected(Exception e) => e is IOException
        or InvalidDataException or UnauthorizedAccessException or Win32Exception
        or ArgumentException or JsonException or KeyNotFoundException
        or InvalidOperationException or PlatformNotSupportedException;
}
