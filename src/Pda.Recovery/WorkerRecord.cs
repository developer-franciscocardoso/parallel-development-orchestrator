namespace Pda.Recovery;

public sealed record WorkerRecord(string AttemptId, string Worktree,
    string Branch, string BaseCommit, int ProcessId,
    DateTimeOffset ProcessStartedUtc, string JobName,
    string StandardOutputPath, string StandardErrorPath)
{
    public const string ArtifactKind = "worker";
}

public enum TreeCondition { Unknown, Live, Stopped }

public sealed record WorkerObservation(TreeCondition Tree,
    bool RepositoryIntact, string Evidence);

public interface IWorkerInspector
{
    WorkerObservation Inspect(WorkerRecord worker);
}
