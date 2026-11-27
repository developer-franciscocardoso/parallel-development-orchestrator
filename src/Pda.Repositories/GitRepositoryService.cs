namespace Pda.Repositories;

public sealed partial class GitRepositoryService
{
    private const string AttemptPrefix = "pda/attempt/";
    private readonly GitProcess git = new(GitProcess.FindExecutable());

    public RepositoryBaseline Register(string root)
    {
        var state = Inspect(root);
        if (!state.IsClean || state.Branch is null)
            throw new GitRepositoryException(
                "Registration requires a clean local branch.");

        return new RepositoryBaseline(state.Repository, state.Branch,
            state.Commit);
    }

    public WorktreeState Inspect(string root)
    {
        var repository = Identify(root);
        var branch = git.Run(repository.Root, "symbolic-ref", "--quiet",
            "HEAD");
        var name = branch.ExitCode switch
        {
            0 => LocalBranch(Line(branch.Output)),
            1 => null,
            _ => throw GitProcess.Error("symbolic-ref", branch)
        };
        var head = Commit(repository.Root, Line(git.Require(repository.Root,
            "rev-parse", "--verify", "HEAD")));
        var index = git.Require(repository.Root, "ls-files", "-v",
            "--stage", "-z");
        var flags = Fields(index);
        var complete = flags.All(entry =>
            entry.StartsWith("H ", StringComparison.Ordinal) &&
            !entry.StartsWith("H 120000 ", StringComparison.Ordinal) &&
            !entry.StartsWith("H 160000 ", StringComparison.Ordinal));
        var isClean = complete && git.Require(repository.Root, "status",
            "--porcelain=v1", "-z", "--untracked-files=all",
            "--ignore-submodules=all").Length == 0;
        return new WorktreeState(repository, name, head, isClean);
    }

    public AttemptWorktree PlanAttempt(RepositoryBaseline baseline,
        string worktreeRoot, string taskId, string attemptId)
    {
        ValidateSegment(taskId);
        ValidateSegment(attemptId);
        var destination = LocalPaths.DirectoryPath(worktreeRoot);
        var path = Path.Combine(destination,
            AttemptDirectory(taskId, attemptId));
        var branch = AttemptPrefix + taskId + "/" + attemptId;
        var attempt = new AttemptWorktree(baseline, destination, path, branch);
        ValidateAttempt(attempt);
        return attempt;
    }

    public WorktreeState CreateAttempt(AttemptWorktree attempt)
    {
        var repository = ValidateAttempt(attempt);
        var current = Register(repository.Root);
        if (current.Branch != attempt.Baseline.Branch ||
            current.Commit != attempt.Baseline.Commit)
            throw new GitRepositoryException("The source baseline changed.");

        git.Require(repository.Root, "worktree", "add", "--no-track", "-b",
            attempt.Branch, "--", attempt.Path, attempt.Baseline.Commit);

        var state = Inspect(attempt.Path);
        if (!state.IsClean || state.Branch != attempt.Branch ||
            state.Commit != attempt.Baseline.Commit ||
            state.Repository.CommonDirectory != repository.CommonDirectory)
            throw new GitRepositoryException(
                "Created attempt did not match the pinned checkout.");

        return state;
    }

    public IReadOnlyList<WorktreeRegistration> ListWorktrees(
        RepositoryIdentity repository)
    {
        var verified = Verify(repository);
        var result = git.Require(verified.Root, "worktree", "list",
            "--porcelain", "-z");
        var worktrees = new List<WorktreeRegistration>();
        foreach (var entry in result.Split("\0\0",
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split('\0');
            var path = LocalPaths.FullPath(Field(fields, "worktree ") ??
                throw new GitRepositoryException("Missing worktree path."));
            if (Directory.Exists(path))
                path = LocalPaths.DirectoryPath(path);
            var branch = Field(fields, "branch ");
            var commit = Field(fields, "HEAD ");
            worktrees.Add(new WorktreeRegistration(path,
                branch is null ? null : LocalBranch(branch), commit,
                Flag(fields, "locked"), Flag(fields, "prunable")));
        }

        return worktrees;
    }

    public bool IsAncestor(RepositoryIdentity repository, string ancestor,
        string descendant)
    {
        var verified = Verify(repository);
        var root = verified.Root;
        var result = git.Run(root, "merge-base", "--is-ancestor",
            Commit(root, ancestor), Commit(root, descendant));
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw GitProcess.Error("merge-base", result)
        };
    }

    public IReadOnlyList<GitPathChange> ChangedPaths(
        RepositoryIdentity repository, string baseline, string candidate)
    {
        var verified = Verify(repository);
        var root = verified.Root;
        var output = git.Require(root, "diff", "--name-status", "-z",
            "--no-ext-diff", "--no-textconv", "--no-color",
            "--ignore-submodules=none", "--find-renames",
            Commit(root, baseline), Commit(root, candidate), "--");

        var fields = Fields(output);
        var changes = new List<GitPathChange>();
        for (var index = 0; index < fields.Length;)
        {
            var status = fields[index++];
            if (status.Length == 0 || index >= fields.Length)
                throw new GitRepositoryException("Malformed Git diff output.");

            var path = fields[index++];
            var (oldPath, newPath) = status[0] switch
            {
                'A' => (null, path),
                'D' => (path, null),
                'C' or 'R' when index < fields.Length =>
                    (path, fields[index++]),
                'M' or 'T' => (path, path),
                _ => throw new GitRepositoryException("Unexpected diff status.")
            };
            changes.Add(new GitPathChange(status, oldPath, newPath));
        }

        return changes;
    }

    private RepositoryIdentity ValidateAttempt(AttemptWorktree attempt)
    {
        var baseline = attempt.Baseline;
        var repository = Verify(baseline.Repository);
        Commit(repository.Root, baseline.Commit);
        if (!attempt.Branch.StartsWith(AttemptPrefix, StringComparison.Ordinal))
            throw new GitRepositoryException("Invalid attempt branch.");
        var parts = attempt.Branch[AttemptPrefix.Length..].Split('/');
        if (parts.Length != 2)
            throw new GitRepositoryException("Invalid attempt branch.");
        ValidateSegment(parts[0]);
        ValidateSegment(parts[1]);

        var root = LocalPaths.DirectoryPath(attempt.WorktreeRoot);
        var expected = Path.Combine(root, AttemptDirectory(parts[0], parts[1]));
        if (expected != attempt.Path ||
            LocalPaths.Overlaps(root, repository.CommonDirectory))
            throw new GitRepositoryException("Invalid attempt destination.");

        LocalPaths.RequireOutsideCheckout(root);
        if (ListWorktrees(repository).Any(existing =>
                LocalPaths.Contains(existing.Path, root) ||
                LocalPaths.Overlaps(expected, existing.Path)))
            throw new GitRepositoryException(
                "Attempt storage must be outside existing worktrees.");

        LocalPaths.RequireAbsent(expected);
        return repository;
    }

    private RepositoryIdentity Identify(string path)
    {
        var root = LocalPaths.DirectoryPath(path);
        var marker = Path.Combine(root, ".git");
        LocalPaths.RejectReparsePoints(marker);
        if (!Directory.Exists(marker) && !File.Exists(marker))
            throw new GitRepositoryException("The Git root must be supplied.");

        var topLevel = LocalPaths.DirectoryPath(Line(git.Require(root,
            "rev-parse", "--show-toplevel")));
        if (root != topLevel)
            throw new GitRepositoryException("The Git root must be supplied.");

        var common = LocalPaths.DirectoryPath(Line(git.Require(root,
            "rev-parse", "--path-format=absolute", "--git-common-dir")));
        var gitDir = LocalPaths.DirectoryPath(Line(git.Require(root,
            "rev-parse", "--absolute-git-dir")));
        LocalPaths.RejectReparsePoints(Path.Combine(common, "config"));
        LocalPaths.RejectReparsePoints(Path.Combine(gitDir, "config.worktree"));
        LocalPaths.RejectReparsePoints(Path.Combine(common, "objects"));
        var controls = git.Require(root, "config", "--null", "--name-only",
            "--list", "--no-includes");

        if (Fields(controls).Any(UnsafeConfig))
            throw new GitRepositoryException(
                "Repository contains unsupported Git configuration.");

        return new RepositoryIdentity(topLevel, common);
    }

    private static bool UnsafeConfig(string key)
        => key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) ||
           key.StartsWith("merge.", StringComparison.OrdinalIgnoreCase) &&
               key.EndsWith(".driver", StringComparison.OrdinalIgnoreCase) ||
           key.StartsWith("include.", StringComparison.OrdinalIgnoreCase) ||
           key.StartsWith("includeif.", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("core.worktree", StringComparison.OrdinalIgnoreCase);

    private RepositoryIdentity Verify(RepositoryIdentity repository)
    {
        var actual = Identify(repository.Root);
        if (actual.CommonDirectory != repository.CommonDirectory)
            throw new GitRepositoryException("Repository identity changed.");

        return actual;
    }

    private string Commit(string root, string commit)
    {
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
            throw new GitRepositoryException("A full commit SHA is required.");
        var actual = Line(git.Require(root, "rev-parse", "--verify",
            "--end-of-options", commit + "^{commit}"));
        if (!actual.Equals(commit, StringComparison.OrdinalIgnoreCase))
            throw new GitRepositoryException("A full commit SHA is required.");
        return actual;
    }

    private static string LocalBranch(string reference)
    {
        const string prefix = "refs/heads/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal) ||
            reference.Length <= prefix.Length)
            throw new GitRepositoryException("A local branch is required.");

        return reference[prefix.Length..];
    }

    private static string AttemptDirectory(string taskId, string attemptId)
        => $"attempt-{taskId.Length}-{taskId}-{attemptId}";

    private static void ValidateSegment(string value)
    {
        if (value.Length is < 1 or > 48 ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character == '-' || character == '_'))
            throw new GitRepositoryException("Invalid task or attempt ID.");
    }

    private static string[] Fields(string output)
    {
        if (output.Length == 0)
            return [];
        if (!output.EndsWith('\0'))
            throw new GitRepositoryException("Malformed Git output.");
        return output[..^1].Split('\0');
    }

    private static string Line(string output)
    {
        var line = output.EndsWith("\r\n", StringComparison.Ordinal)
            ? output[..^2] : output.EndsWith('\n') ? output[..^1] : "";
        if (line.Length == 0 || line.Any(char.IsControl))
            throw new GitRepositoryException("Malformed Git output.");
        return line;
    }

    private static string? Field(string[] entries, string prefix)
    {
        var field = entries.FirstOrDefault(entry =>
            entry.StartsWith(prefix, StringComparison.Ordinal));
        return field?[prefix.Length..];
    }

    private static bool Flag(string[] entries, string key)
        => entries.Any(entry => entry == key ||
            entry.StartsWith(key + " ", StringComparison.Ordinal));
}
