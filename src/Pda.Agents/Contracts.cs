using System.Text.Json;
using Pda.Execution;

namespace Pda.Agents;

public sealed record AgentRequest(string TaskId, string AttemptId,
    string Worktree, string OutputLog, string ErrorLog, string ReportPath,
    string Prompt, TimeSpan Timeout, string? Model = null);

public sealed record AgentCallbacks(
    Func<SupervisedIdentity, CancellationToken, Task> Started,
    Action<AgentEvent> Event);

public sealed record AgentEvent(string Type, JsonElement Payload);
public sealed record TokenUsage(long Input, long CachedInput, long Output)
{
    public long Total => checked(Input + Output);
}

public enum RuntimeError
{
    None, PolicyDenied, Unavailable, Authentication, Failed,
    Cancelled, Timeout, InvalidReport, Protocol
}

public sealed record RuntimeDiscovery(string Id, string? Version,
    bool Authenticated, IReadOnlyList<string> Capabilities,
    RuntimeError Error, string Detail)
{
    public bool Ready => Error == RuntimeError.None;
}

public sealed record RuntimeResult(RuntimeError Error, string Detail,
    TokenUsage? Usage, WorkerReport? Report, ProcessOutcome? Process,
    bool Simulated)
{
    public bool PreflightRejected { get; init; }
}

public interface IAgentRuntime
{
    string Id { get; }
    Task<RuntimeDiscovery> DiscoverAsync(AgentRequest request,
        CancellationToken cancellationToken = default);
    Task<RuntimeResult> RunAsync(AgentRequest request, AgentCallbacks callbacks,
        CancellationToken cancellationToken = default);
}
