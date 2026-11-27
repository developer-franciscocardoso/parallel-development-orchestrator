namespace Pda.Repositories;

public sealed record GitCandidate(AttemptWorktree Attempt, string Commit,
    IReadOnlyList<GitPathChange> Changes);

public sealed record GitMerge(PlanWorktree Plan, GitCandidate Candidate,
    string PreviousHead, string Commit, string Path, string Branch);
