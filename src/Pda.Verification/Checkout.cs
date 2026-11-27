using Pda.Repositories;
using Pda.Security;

namespace Pda.Verification;

internal static class Checkout
{
    internal static RepositoryIdentity Verify(VerificationInput input)
    {
        var binding = input.Binding;
        if (CandidateBinding.For(binding.BaseCommit, binding.CandidateCommit,
            input.Contract) != binding)
            throw new InvalidDataException("Task contract digest changed.");
        var git = new GitRepositoryService();
        var state = git.Inspect(TrustedPaths.Directory(input.Worktree));
        if (!state.IsClean || state.Commit != binding.CandidateCommit ||
            !git.IsAncestor(state.Repository, binding.BaseCommit, state.Commit))
            throw new InvalidDataException("Candidate checkout changed.");
        return state.Repository;
    }

    internal static IReadOnlyList<GitPathChange> Changes(VerificationInput input)
    {
        var repo = Verify(input);
        var changes = new GitRepositoryService().ChangedPaths(repo,
            input.Binding.BaseCommit, input.Binding.CandidateCommit);
        if (!changes.SequenceEqual(input.Changes) || changes.Count == 0)
            throw new InvalidDataException("Candidate change list mismatch.");
        return changes;
    }

    internal static TrackedSnapshot Fresh(VerificationInput input)
    {
        var snapshot = new GitRepositoryService().TrackedFiles(input.Worktree);
        if (snapshot.Commit != input.Binding.CandidateCommit)
            throw new InvalidDataException("Candidate tree changed.");
        var files = snapshot.Files.Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var slash = file.LastIndexOf('/');
            while (slash >= 0 && directories.Add(file[..slash]))
                slash = file.LastIndexOf('/', slash - 1);
        }
        var pending = new Stack<string>();
        pending.Push(input.Worktree);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(input.Worktree, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relative == ".git") continue;
                if (++count > 40_000)
                    throw new InvalidDataException("Checkout exceeds bounds.");
                var flags = File.GetAttributes(entry);
                if (flags.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Linked checkout entry.");
                if (flags.HasFlag(FileAttributes.Directory))
                {
                    if (!directories.Contains(relative))
                        throw new InvalidDataException("Untracked directory.");
                    pending.Push(entry);
                }
                else if (!files.Remove(relative))
                    throw new InvalidDataException("Untracked checkout file.");
            }
        }
        if (files.Count != 0)
            throw new InvalidDataException("Candidate files missing.");
        return snapshot;
    }
}
