namespace Pda.Execution;

public sealed record ProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    TimeSpan Timeout,
    string StandardInput = "",
    long MaxLogBytes = 4 * 1024 * 1024,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record SupervisedIdentity(
    int ProcessId, DateTimeOffset StartedUtc, string JobName);

public enum ProcessTreeState { Unknown, Live, Stopped }

public sealed record ProcessOutcome(
    SupervisedIdentity Identity,
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    bool TreeStopped,
    bool OutputTruncated,
    bool ErrorTruncated);
