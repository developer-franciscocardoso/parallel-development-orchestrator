namespace Pda.Repositories;

public sealed record RepositoryIdentity(string Root, string CommonDirectory);

public sealed record RepositoryBaseline(RepositoryIdentity Repository,
    string Branch, string Commit);

public sealed record AttemptWorktree(RepositoryBaseline Baseline,
    string WorktreeRoot, string Path, string Branch);

public sealed record WorktreeState(RepositoryIdentity Repository,
    string? Branch, string Commit, bool IsClean);

public sealed record GitPathChange(string Status, string? OldPath,
    string? NewPath);

public sealed class GitRepositoryException(string message,
    Exception? innerException = null) : Exception(message, innerException);

public sealed record WorktreeRegistration(string Path, string? Branch,
    string? Commit, bool IsLocked, bool IsPrunable);
