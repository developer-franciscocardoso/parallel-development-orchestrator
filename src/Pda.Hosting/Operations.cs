using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pda.Application;
using Pda.Core;
using Pda.Quality;
using Pda.Scheduling;
using Pda.Verification;

namespace Pda.Hosting;

public sealed partial class OrchestratorHost
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public long EventCursor() => Store.LatestSequence();

    public IReadOnlyList<JournalEntry> Events(long after, int limit,
        string? plan = null) => Store.ReadJournal(after, limit, plan);

    public PlanReport Report(string plan) => new(FindPlan(plan),
        Store.Contracts(plan), workers.Status(plan), Concurrency(plan),
        IsCancelled(plan), Store.Artifacts<DispatchRecord>(plan, "dispatch"),
        Store.Artifacts<ReviewReport>(plan, "review"),
        Store.Artifacts<IntegratedChange>(plan, "integration"),
        Store.History(plan), Store.Artifacts<QualityFailure>(plan, "quality-failure"),
        ExecutionAnalytics.Measure(Store, plan));

    public string Export(string plan) =>
        JsonSerializer.Serialize(Report(plan), ReportJson);

    private PlanInfo FindPlan(string plan) =>
        Store.Plans().SingleOrDefault(p => p.Id == plan)
        ?? throw new KeyNotFoundException("Unknown plan.");

    public int Concurrency(string plan) => Store.Artifacts<int>(plan, "setting")
        .SingleOrDefault(v => v.Id == "concurrency")?.Data ??
        Settings.DefaultConcurrency;

    public void SetConcurrency(string plan, int value)
    {
        if (value is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(value));
        using var guard = ControlGuard();
        FindPlan(plan);
        var prior = Store.Artifacts<int>(plan, "setting")
            .SingleOrDefault(v => v.Id == "concurrency");
        if ((prior?.Data ?? Settings.DefaultConcurrency) != value)
            Store.PutArtifact(plan, "setting", "concurrency", value,
                prior?.Version ?? 0);
    }

    public void Pause(string plan)
    {
        using var guard = ControlGuard();
        workers.Pause(plan);
    }

    public void Resume(string plan)
    {
        using var guard = ControlGuard();
        if (IsCancelled(plan))
            throw new InvalidOperationException("The plan was cancelled.");
        workers.Resume(plan);
    }

    private bool IsCancelled(string plan) => Store.Artifacts<bool>(plan,
        "control").Any(a => a.Id == "cancelled" && a.Data);

    public void CancelPlan(string plan)
    {
        using var guard = ControlGuard();
        FindPlan(plan);
        if (!IsCancelled(plan))
            Store.PutArtifact(plan, "control", "cancelled", true);
        workers.Pause(plan);
        foreach (var attempt in Store.Attempts(plan).Where(a =>
            a.State is AttemptState.Running or AttemptState.Reserved))
        {
            try { workers.Cancel(plan, attempt.Id); }
            catch (InvalidOperationException) when (Store.Attempts(plan)
                .Any(a => a.Id == attempt.Id && a.State is not
                (AttemptState.Running or AttemptState.Reserved))) { }
        }
    }

    public void CancelAttempt(string plan, string attempt)
    {
        using var guard = ControlGuard();
        workers.Cancel(plan, attempt);
    }

    public Attempt Retry(string plan, string attempt, string reason)
    {
        using var guard = ControlGuard();
        if (IsCancelled(plan))
            throw new InvalidOperationException("The plan was cancelled.");
        var current = Store.Attempts(plan).Single(a => a.Id == attempt);
        return Store.AuthorizeRetry(attempt, current.Version, reason);
    }

    public async Task<ExecutionOutcome> ExecuteAsync(string plan,
        CancellationToken token = default)
    {
        FindPlan(plan);
        if (IsCancelled(plan))
            throw new InvalidOperationException("The plan was cancelled.");
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitor = WatchPlan(plan, active);
        try { return await execution.ExecuteAsync(plan, 6, active.Token); }
        finally
        {
            await active.CancelAsync();
            await monitor;
        }
    }

    private async Task WatchPlan(string plan, CancellationTokenSource active)
    {
        try
        {
            while (!active.IsCancellationRequested)
            {
                await Task.Delay(250, active.Token);
                if (IsCancelled(plan)) await active.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested) { }
        catch
        {
            await active.CancelAsync();
            throw;
        }
    }

    private IDisposable ControlGuard()
    {
        // A native worker can spend time in discovery while holding its startup
        // lock. Stop requests must reach its cancellation monitor immediately.
        var path = Path.Combine(Home, "controls.lock");
        if (Path.Exists(path)) Pda.Security.TrustedPaths.File(path);
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(25);
            }
        }
    }
}
