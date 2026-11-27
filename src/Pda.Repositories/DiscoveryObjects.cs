namespace Pda.Repositories;

public sealed record TrackedObject(string Path, string ObjectId, long Bytes);

public sealed class TrackedSnapshot
{
    private readonly GitProcess git;
    public RepositoryIdentity Repository { get; }
    public string Commit { get; }
    public IReadOnlyList<TrackedObject> Files { get; }

    internal TrackedSnapshot(GitProcess git, RepositoryIdentity repository,
        string commit, List<TrackedObject> files)
    {
        this.git = git;
        Repository = repository;
        Commit = commit;
        Files = files.AsReadOnly();
    }

    public string ReadText(TrackedObject file)
    {
        if (file.Bytes > 262144 || !Files.Contains(file))
            throw new GitRepositoryException("Unsupported discovery object.");
        return git.Require(Repository.Root, "cat-file", "blob", file.ObjectId);
    }
}

public sealed partial class GitRepositoryService
{
    public (RepositoryIdentity Repository, string Commit) DiscoveryHead(
        string root)
    {
        var identity = Identify(root);
        return (identity, Commit(identity.Root,
            Line(git.Require(identity.Root, "rev-parse", "--verify", "HEAD"))));
    }

    public TrackedSnapshot TrackedFiles(string root)
    {
        var (identity, commit) = DiscoveryHead(root);
        var listing = git.Require(identity.Root, "ls-tree", "-r", "-l", "-z",
            commit);
        var result = new List<TrackedObject>();
        var start = 0;
        var count = 0;
        while (start < listing.Length)
        {
            if (++count > 20000)
                throw new GitRepositoryException("Discovery exceeds 20000 entries.");
            var end = listing.IndexOf('\0', start);
            if (end < 0) throw new GitRepositoryException("Invalid Git tree.");
            var line = listing[start..end];
            start = end + 1;
            var tab = line.IndexOf('\t');
            if (tab < 0) throw new GitRepositoryException("Invalid Git tree entry.");
            var fields = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields[0] is not ("100644" or "100755")) continue;
            if (fields.Length != 4 || fields[1] != "blob" ||
                !long.TryParse(fields[3], out var size) || size < 0)
                throw new GitRepositoryException("Invalid Git blob entry.");
            result.Add(new(line[(tab + 1)..], fields[2], size));
        }
        return new(git, identity, commit, result);
    }
}
