using Pda.Application;
using Pda.Core;
using Pda.Quality;
using Pda.Scheduling;
using Pda.Verification;

namespace Pda.Hosting;

public sealed record DoctorReport(bool Ready, bool LiveConfigured,
    string[] Diagnostics);

public sealed record PlanReport(PlanInfo Plan, IReadOnlyList<TaskContract> Tasks,
    ExecutionStatus Status, int Concurrency, bool Cancelled,
    IReadOnlyList<Artifact<DispatchRecord>> Workers,
    IReadOnlyList<Artifact<ReviewReport>> Reviews,
    IReadOnlyList<Artifact<IntegratedChange>> Integrations,
    IReadOnlyList<JournalEntry> History,
    IReadOnlyList<Artifact<QualityFailure>> Failures, PlanMetrics Metrics,
    IReadOnlyList<Artifact<string>> PendingReviews);

public sealed record HostSettings(int DefaultConcurrency = 2,
    bool EnableLiveCodex = false, string? CodexExecutable = null,
    string? CodexHome = null, string? CodexModel = null,
    string[]? ToolDirectories = null, string[]? ReadOnlyDirectories = null);
