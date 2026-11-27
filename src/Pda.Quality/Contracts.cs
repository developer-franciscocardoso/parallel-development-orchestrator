using Pda.Repositories;

namespace Pda.Quality;

public sealed record GitQualityOperations(
    Func<AttemptWorktree, string?, Func<string, bool>, GitCandidate> Capture,
    Func<PlanWorktree, string, GitCandidate, string, GitMerge> PrepareMerge,
    Func<GitMerge, RepositoryBaseline> Advance);

public sealed record IntegratedChange(string AttemptId, string BaseCommit,
    string CandidateCommit, string PreviousHead, string Commit, string Branch);

public sealed record QualityFailure(string Stage, string Detail,
    string Resolution);

internal sealed record RecordedHead(string Commit);
