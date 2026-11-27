using Pda.Core;
using Pda.Persistence;
using Pda.Providers;

namespace Pda.Admission.Tests;

public sealed class AdmissionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private readonly Clock clock = new();
    private StateStore Store => new(Path.Combine(root, "state.db"));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TaskContract Task(string id, int priority = 0,
        long tokens = 30) =>
        new(id, id, [], [id], [], [], [id], [new("test", [])], [id],
            Risk.Low, 2, 10, priority, "sim", tokens);

    private AdmissionService Service(int concurrency = 6, int rpm = 100,
        long tpm = 1000, HostPressure? host = null)
    {
        var config = Path.Combine(root, "providers.json");
        File.WriteAllText(config, $$"""
            {"providers":[{"id":"p","endpoint":"https://example.com",
            "maxConcurrentRequests":{{concurrency}},
            "requestsPerMinute":{{rpm}},"tokensPerMinute":{{tpm}}}],
            "models":[{"id":"m","providerId":"p","runtimeIds":["sim"],
            "contextTokens":100,"maxOutputTokens":10}]}
            """);
        return new(Store, ProviderRegistry.Load(config, ["sim"]),
            () => host ?? new(2, 2, 8L << 30, 100), clock);
    }

    private void Plan(string plan, params TaskContract[] tasks) =>
        Store.CreatePlan("project", root, plan, tasks, 10000);

    private Reservation? Reserve(AdmissionService service, string plan)
    {
        using var guard = Store.AcquireStartupGuard();
        return service.ReserveNext(plan, _ => "m");
    }

    [Fact]
    public void SharedProviderCapsSurviveRestart()
    {
        Plan("one", Task("a"));
        Plan("two", Task("b"));
        Assert.NotNull(Reserve(Service(concurrency: 1), "one"));
        Assert.Null(Reserve(Service(concurrency: 1), "two"));
        Assert.Empty(Store.Attempts("two"));
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(100, 59)]
    public void MinuteLimitsIncludeCompletedReservations(int rpm, int tpm)
    {
        Plan("one", Task("a"));
        Plan("two", Task("b"));
        var service = Service(rpm: rpm, tpm: tpm);
        var a = Reserve(service, "one")!.Attempt;
        Store.Transition(a.Id, a.Version, AttemptState.Cancelled, "stopped", 0);
        clock.Now += TimeSpan.FromSeconds(59);
        Assert.Null(Reserve(service, "two"));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.NotNull(Reserve(service, "two"));
    }

    [Fact]
    public void ChargesKnownOverrunAndRejectsOversizedModelInput()
    {
        Plan("one", Task("a"));
        Plan("two", Task("b"), Task("large", priority: 1, tokens: 101));
        var service = Service(tpm: 100);
        var a = Reserve(service, "one")!.Attempt;
        Store.Transition(a.Id, a.Version, AttemptState.Failed, "spent", 75);
        Assert.Null(Reserve(service, "two"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal("b", Reserve(service, "two")?.Contract.Id);
    }

    [Fact]
    public void UnknownCostAndOrphanReservationsBlockOtherPlans()
    {
        Plan("one", Task("a"));
        Plan("two", Task("b"));
        var a = Store.Reserve("one", "a", "interrupted")!;
        Assert.Null(Reserve(Service(), "two"));
        a = Store.Transition(a.Id, a.Version, AttemptState.Failed, "unknown");
        Assert.Null(Reserve(Service(), "two"));
        Store.Reconcile(a.Id, a.Version, "confirmed no spend", 0, true);
        Assert.NotNull(Reserve(Service(), "two"));
    }

    [Fact]
    public void PriorityAndRoundRobinAcrossEligiblePlans()
    {
        Plan("one", Task("a"), Task("b", 20));
        Plan("two", Task("c"));
        var service = Service();
        using var guard = Store.AcquireStartupGuard();
        string[] plans = ["two", "one"];
        Assert.Equal("b", service.ReserveNext(plans, (_, _) => "m")?.Contract.Id);
        Assert.Equal("c", service.ReserveNext(plans, (_, _) => "m")?.Contract.Id);
        Assert.Equal("a", service.ReserveNext(plans, (_, _) => "m")?.Contract.Id);
    }

    [Fact]
    public void DeadlinesAndUnavailableCapabilitiesPreventReservations()
    {
        Plan("one", Task("a"));
        using var guard = Store.AcquireStartupGuard();
        var service = Service();
        Assert.Null(service.ReserveNext("one", _ => "m", deadline: clock.Now));
        Assert.Null(service.ReserveNext("one", _ => null));
        Assert.Empty(Store.Attempts("one"));
    }

    [Theory]
    [InlineData(95, 5, 8000, 10, 0)]
    [InlineData(0, 96, 8000, 10, 0)]
    [InlineData(0, 0, 511, 10, 0)]
    [InlineData(0, 0, 8000, 2048, 0)]
    [InlineData(80, 0, 8000, 50, 1)]
    [InlineData(20, 70, 1600, 50, 3)]
    [InlineData(20, 70, 8000, 50, 6)]
    public void CapacityReflectsMeasuredResources(int cpu, int disk, int mib,
        int processes, int expected)
    {
        Assert.Equal(expected, new HostPressure(cpu, disk, (long)mib << 20,
            processes).Capacity(6));
    }

    [Fact]
    public void HostCapacityIsEnforcedAtReservation()
    {
        Plan("one", Task("a"), Task("b"));
        var service = Service(host: new(80, 0, 8L << 30, 50));
        Assert.NotNull(Reserve(service, "one"));
        Assert.Null(Reserve(service, "one"));
    }

    [Fact]
    public void LiveWorkerRequiresPositiveTokenReservation()
    {
        Plan("one", Task("live", tokens: 0) with {Runtime = "codex"});
        Assert.Null(Reserve(Service(), "one"));
        Assert.Empty(Store.Attempts("one"));
    }

    public void Dispose() => Directory.Delete(root, true);
}
