namespace Pda.Core;

public enum Risk { Low, Medium, High }

public sealed record VerificationCommand(string Executable, string[] Arguments);

public sealed record TaskContract(string Id, string Objective,
    string[] Dependencies, string[] Owned, string[] ReadOnly, string[] Excluded,
    string[] Criteria, VerificationCommand[] Verification, string[] Deliverables,
    Risk Risk, int MaxAttempts, int TimeoutSeconds, int Priority, string Runtime,
    long TokenReservation);

public enum AttemptState
{
    Reserved, Running, AwaitingVerification, Verified, Integrated,
    Failed, Cancelled, Interrupted, Blocked, RetryAuthorized
}

public sealed record Attempt(string Id, string PlanId, string TaskId,
    int Number, AttemptState State, long Version, long ReservedTokens,
    long? UsedTokens);

public sealed record JournalEntry(long Sequence, string? PlanId, string? TaskId,
    string? AttemptId, string Kind, string Payload, DateTimeOffset Timestamp);

public sealed record ProjectInfo(string Id, string Root);
public sealed record PlanInfo(string Id, string ProjectId, long TokenBudget,
    long? ProfileVersion = null, string? BaselineCommit = null);
public interface IProjectProfile
{
    string Commit { get; }
}
public sealed record Artifact<T>(string Id, T Data, long Version);

public interface IWorkerIdentity
{
    public const string ArtifactKind = "worker";
    string AttemptId { get; }
}

public interface IStateStore
{
    Artifact<T> SaveProjectProfile<T>(string project, T profile,
        long expectedVersion) where T : IProjectProfile;
    Artifact<T>? LatestProjectProfile<T>(string project);
    IReadOnlyList<Artifact<T>> ProjectProfiles<T>(string project);
    IDisposable AcquireStartupGuard();
    void RegisterProject(string id, string repository);
    IReadOnlyList<ProjectInfo> Projects();
    IReadOnlyList<PlanInfo> Plans();
    Artifact<T> PutArtifact<T>(string plan, string kind, string id,
        T data, long expectedVersion = 0);
    IReadOnlyList<Artifact<T>> Artifacts<T>(string plan, string kind);
    Attempt Reconcile(string attemptId, long expectedVersion,
        string evidence, long usedTokens, bool confirmedStopped);
    Attempt AuthorizeRetry(string attemptId, long expectedVersion,
        string reason);
    void CreatePlan(string projectId, string repository, string planId,
        IReadOnlyList<TaskContract> tasks, long tokenBudget,
        long? profileVersion = null);
    IReadOnlyList<TaskContract> Contracts(string planId);
    IReadOnlyList<Attempt> Attempts(string planId);
    Attempt? Reserve(string planId, string taskId, string requestKey,
        int concurrency = 6);
    Attempt RejectPreflight<T>(string attemptId, long expectedVersion,
        T result, string detail, bool policyDenied);
    Attempt Transition(string attemptId, long expectedVersion,
        AttemptState state, string evidence, long? usedTokens = null);
    IReadOnlyList<JournalEntry> History(string? planId, long after = 0);
}
