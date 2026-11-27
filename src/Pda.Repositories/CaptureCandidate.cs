namespace Pda.Repositories;

public sealed partial class GitRepositoryService
{
    public GitCandidate CaptureCandidate(AttemptWorktree attempt,
        string? reportedCommit, Func<string, bool> permittedPath)
    {
        ArgumentNullException.ThrowIfNull(permittedPath);
        var state = OpenWorker(attempt);
        var root = attempt.Path;
        var basis = attempt.Baseline.Commit;
        var marker = "PDA capture " + attempt.Branch;
        RejectUnsafeIndex(root);
        if (git.Require(root, "ls-files", "--others", "--ignored",
            "--exclude-standard", "-z").Length != 0)
            throw new GitRepositoryException(
                "Remove ignored worker files before capturing the candidate.");

        var head = state.Commit;
        if (!string.IsNullOrEmpty(reportedCommit) && reportedCommit != head)
            throw new GitRepositoryException("Worker commit mismatch.");
        if (head != basis)
        {
            if (!state.IsClean || Parents(root, head) != basis ||
                string.IsNullOrEmpty(reportedCommit) &&
                Line(git.Require(root, "show", "-s", "--format=%s", head))
                    != marker)
                throw new GitRepositoryException("Unrecognized worker commit.");
            return CheckedCandidate(attempt, head, permittedPath);
        }
        if (!string.IsNullOrEmpty(reportedCommit))
            throw new GitRepositoryException("Reported commit has no changes.");

        var changed = Fields(git.Require(root, "diff", "--name-only", "-z",
            "--no-renames", "--no-ext-diff", "--no-textconv", basis, "--"));
        var added = Fields(git.Require(root, "ls-files", "--others",
            "--exclude-standard", "-z"));
        if (changed.Length + added.Length == 0)
            throw new GitRepositoryException("Worker produced no changes.");
        foreach (var path in changed.Concat(added).Distinct())
            CheckCandidatePath(root, path, permittedPath);

        git.Require(root, "add", "--all", "--", ".");
        RejectUnsafeIndex(root);
        var tree = Line(git.Require(root, "write-tree"));
        var commit = Line(CoordinatorGit(root, "commit-tree", tree, "-p",
            basis, "-m", marker));
        var result = CheckedCandidate(attempt, commit, permittedPath);
        git.Require(root, "update-ref", "refs/heads/" + attempt.Branch,
            commit, basis);
        var final = OpenWorker(attempt);
        if (!final.IsClean || final.Commit != commit)
            throw new GitRepositoryException("Worker changed during capture.");
        return result;
    }

    private GitCandidate CheckedCandidate(AttemptWorktree attempt,
        string commit, Func<string, bool> permittedPath)
    {
        var changes = ChangedPaths(attempt.Baseline.Repository,
            attempt.Baseline.Commit, commit);
        if (changes.Count == 0)
            throw new GitRepositoryException("Empty candidate.");
        foreach (var change in changes)
        {
            if (change.OldPath is string old)
                CheckCandidatePath(attempt.Path, old, permittedPath);
            if (change.NewPath is string current)
                CheckCandidatePath(attempt.Path, current, permittedPath);
        }
        return new(attempt, commit, changes);
    }

    private static void CheckCandidatePath(string root, string path,
        Func<string, bool> permittedPath)
    {
        var parts = path.Split('/');
        if (Path.IsPathFullyQualified(path) || parts.Any(p =>
            p.Length == 0 || p is "." or ".." ||
            p.TrimEnd(' ', '.') != p || p.Any(c =>
                char.IsControl(c) || "<>:\"\\|?*".Contains(c)) ||
            p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new GitRepositoryException("Unsafe changed path.");
        LocalPaths.RejectReparsePoints(Path.Combine(root, path));
        if (!permittedPath(path))
            throw new GitRepositoryException("Change outside task scope.");
    }

    private WorktreeState OpenWorker(AttemptWorktree attempt)
    {
        var repo = Verify(attempt.Baseline.Repository);
        Commit(repo.Root, attempt.Baseline.Commit);
        var root = LocalPaths.DirectoryPath(attempt.WorktreeRoot);
        LocalPaths.RequireOutsideCheckout(root);
        var parts = attempt.Branch.Split('/');
        if (parts.Length != 4 || parts[0] != "pda" || parts[1] != "attempt")
            throw new GitRepositoryException("Invalid worker branch.");
        ValidateSegment(parts[2]);
        ValidateSegment(parts[3]);
        if (attempt.Path != Path.Combine(root,
                AttemptDirectory(parts[2], parts[3])) ||
            LocalPaths.Overlaps(attempt.Path, repo.Root) ||
            LocalPaths.Overlaps(root, repo.CommonDirectory))
            throw new GitRepositoryException("Unexpected worker path.");
        return RegisteredCheckout(repo, attempt.Path, attempt.Branch);
    }

    private WorktreeState RegisteredCheckout(RepositoryIdentity repo,
        string path, string branch)
    {
        var entry = ListWorktrees(repo).SingleOrDefault(w =>
            LocalPaths.Equal(w.Path, path));
        if (entry is null || entry.Branch != branch || entry.IsLocked ||
            entry.IsPrunable)
            throw new GitRepositoryException("Unrecognized linked checkout.");
        var state = Inspect(path);
        if (state.Repository.CommonDirectory != repo.CommonDirectory ||
            state.Branch != branch || state.Commit != entry.Commit)
            throw new GitRepositoryException("Checkout identity changed.");
        return state;
    }

    private void RejectUnsafeIndex(string root)
    {
        var files = Fields(git.Require(root, "ls-files", "-v", "--stage",
            "-z"));
        if (files.Any(f => !f.StartsWith("H 100644 ", StringComparison.Ordinal)
            && !f.StartsWith("H 100755 ", StringComparison.Ordinal)))
            throw new GitRepositoryException("Unsupported Git index mode.");
    }

    private string Parents(string root, string commit) =>
        Line(git.Require(root, "show", "-s", "--format=%P", commit));

    private string CoordinatorGit(string root, params string[] args) =>
        git.Require(root, ["-c", "user.name=PDA coordinator", "-c",
            "user.email=pda@localhost", "-c", "commit.gpgsign=false",
            "-c", "rerere.enabled=false", .. args]);
}
