using Pda.Core;
using Pda.Providers;

namespace Pda.Admission;

public sealed record Reservation(Attempt Attempt, TaskContract Contract,
    ResolvedModel Model);

public sealed record Grant(string ProviderId, string ModelId,
    DateTimeOffset At, long Tokens);

public sealed class AdmissionService(IStateStore store,
    ProviderRegistry providers, Func<HostPressure>? measure = null,
    TimeProvider? clock = null)
{
    public const string ArtifactKind = "admission";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<HostPressure> sample = measure ?? HostPressure.Measure;

    // Caller holds the startup guard through identity publication.
    public Reservation? ReserveNext(string planId,
        Func<TaskContract, string?> modelFor, int concurrency = 6,
        DateTimeOffset? deadline = null) =>
        ReserveNext([planId], (_, task) => modelFor(task), concurrency, deadline);

    public Reservation? ReserveNext(IReadOnlyCollection<string> planIds,
        Func<string, TaskContract, string?> modelFor, int concurrency = 6,
        DateTimeOffset? deadline = null)
    {
        if (concurrency is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(concurrency));

        var capacity = sample().Capacity(concurrency);
        var now = time.GetUtcNow();
        if (capacity == 0 || deadline <= now) return null;
        var ledger = new List<Entry>();
        foreach (var plan in store.Plans())
        {
            var grants = store.Artifacts<Grant>(plan.Id, ArtifactKind)
                .ToDictionary(g => g.Id);
            foreach (var attempt in store.Attempts(plan.Id))
            {
                grants.TryGetValue(attempt.Id, out var grant);
                if (Uncertain(attempt, grant?.Data)) return null;
                ledger.Add(new(attempt, grant?.Data));
            }
        }

        // Least recently dispatched plan wins. Within a fixed plan, equal
        // priorities serve fresh tasks before explicitly authorized retries.
        var queue = planIds.Distinct(StringComparer.Ordinal)
            .OrderBy(id => ledger.Where(e => e.Attempt.PlanId == id)
                .Max(e => e.Grant?.At))
            .ThenBy(id => ledger.Count(e => e.Attempt.PlanId == id))
            .ThenBy(id => id, StringComparer.Ordinal);
        foreach (var plan in queue)
        {
            var reservation = Select(plan, modelFor, ledger, now, capacity,
                deadline);
            if (reservation != null) return reservation;
        }
        return null;
    }

    private Reservation? Select(string plan,
        Func<string, TaskContract, string?> modelFor, List<Entry> ledger,
        DateTimeOffset now, int capacity, DateTimeOffset? deadline)
    {
        var previous = ledger.Where(e => e.Attempt.PlanId == plan)
            .ToLookup(e => e.Attempt.TaskId);
        var tasks = store.Contracts(plan).OrderByDescending(t => t.Priority)
            .ThenBy(t => previous[t.Id].Count())
            .ThenBy(t => t.Id, StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (previous[task.Id].Any(e =>
                e.Attempt.State != AttemptState.RetryAuthorized)) continue;
            if (modelFor(plan, task) is not { } id) continue;
            var model = providers.Resolve(task.Runtime, id);
            if (!CanAfford(ledger, model, task.TokenReservation, now)) continue;
            if (deadline <= time.GetUtcNow()) return null;
            var attempt = store.Reserve(plan, task.Id,
                Guid.NewGuid().ToString("N"), capacity);
            if (attempt == null) continue;
            var grant = new Grant(model.Provider.Id, id, time.GetUtcNow(),
                task.TokenReservation);
            store.PutArtifact(plan, ArtifactKind, attempt.Id, grant);
            return new(attempt, task, model);
        }
        return null;
    }

    private sealed record Entry(Attempt Attempt, Grant? Grant);

    private static bool Uncertain(Attempt attempt, Grant? grant) =>
        attempt.State == AttemptState.Interrupted ||
        (attempt.State is AttemptState.Running or AttemptState.Reserved
            ? grant == null : attempt.UsedTokens == null);

    private static bool CanAfford(List<Entry> ledger, ResolvedModel model,
        long tokens, DateTimeOffset now)
    {
        var provider = model.Provider;
        if (tokens > model.Model.ContextTokens) return false;
        var entries = ledger.Where(e => e.Grant?.ProviderId == provider.Id);
        if (entries.Count(e => e.Attempt.State is AttemptState.Running
            or AttemptState.Reserved) >= provider.MaxConcurrentRequests)
            return false;
        var recent = entries.Where(e =>
            e.Grant!.At > now - TimeSpan.FromMinutes(1)).ToArray();
        if (recent.Length >= provider.RequestsPerMinute) return false;
        var remaining = provider.TokensPerMinute;
        foreach (var entry in recent)
        {
            var cost = Math.Max(entry.Grant!.Tokens,
                entry.Attempt.UsedTokens ?? 0);
            if (cost > remaining) return false;
            remaining -= cost;
        }
        return tokens <= remaining;
    }
}
