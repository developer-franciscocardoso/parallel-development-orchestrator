using System.Security.Cryptography;
using System.Text.Json;
using Pda.Core;
using Pda.Repositories;

namespace Pda.Verification;

public sealed record CandidateBinding(string BaseCommit, string CandidateCommit,
    string ContractDigest)
{
    public static CandidateBinding For(string baseCommit, string candidateCommit,
        TaskContract contract) => new(baseCommit, candidateCommit,
            Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(contract))));
}

public sealed record VerificationInput(CandidateBinding Binding,
    TaskContract Contract, string Worktree, string LogDirectory,
    IReadOnlyList<GitPathChange> Changes);

public sealed record CommandEvidence(VerificationCommand Command, int ExitCode,
    long DurationMs, string OutputLog, string ErrorLog, string OutputSha256,
    string ErrorSha256, bool TimedOut, bool Cancelled, bool Truncated);

public sealed record CheckReport(CandidateBinding Binding, bool Passed,
    IReadOnlyList<CommandEvidence> Checks, string Detail);

public sealed record ReviewFinding(string Severity, string Location,
    string Impact, string Verification);

public sealed record ReviewReport(CandidateBinding Binding, bool Approved,
    string Reviewer, IReadOnlyList<ReviewFinding> Findings);

public interface IVerificationRunner
{
    Task<CheckReport> RunAsync(VerificationInput input, CancellationToken token);
}

public interface ICandidateReviewer
{
    Task<ReviewReport> ReviewAsync(VerificationInput input,
        CancellationToken token);
}
