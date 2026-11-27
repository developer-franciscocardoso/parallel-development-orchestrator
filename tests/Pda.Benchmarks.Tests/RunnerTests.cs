using System.Text.Json;
using Xunit.Abstractions;

namespace Pda.Benchmarks.Tests;

public sealed class RunnerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportsObservedRawMeasurementsAndInvariants()
    {
        var result = await Runner.RunAsync(1);
        output.WriteLine(JsonSerializer.Serialize(result));
        Assert.True(result.FinishedUtc >= result.StartedUtc);
        Assert.True(result.Environment.LogicalProcessors > 0);
        Assert.Equal(result.Metrics.Count,
            result.Metrics.DistinctBy(m => m.Name).Count());
        foreach (var metric in result.Metrics)
        {
            Assert.NotEmpty(metric.Method);
            Assert.NotEmpty(metric.Unit);
            Assert.All(metric.Samples, v => Assert.True(double.IsFinite(v) && v >= 0));
            if (metric.Samples.Count == 0) Assert.NotNull(metric.Unavailable);
            else Assert.Equal(metric.Samples.Max(), metric.P95);
        }
        Metric Find(string name) => result.Metrics.Single(m => m.Name == name);
        Assert.Equal(2, Find("parallel.peakWorkers").P95);
        Assert.Equal(0, Find("invariant.lostEvents").P95);
        Assert.Equal(0, Find("invariant.duplicateAssignments").P95);
        Assert.True(Find("parallel.throughput").P95 > 0);
        Assert.True(Find("database.transition").P95 > 0);
        if (Find("integration.qualityAndMerge").Samples.Count > 0)
            Assert.Equal(0, Find("invariant.unverifiedIntegrations").P95);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public Task RejectsInvalidIterationCount(int count) =>
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Runner.RunAsync(count));

    [Fact]
    public async Task CancelledRunDoesNotCreateFixtures()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner.RunAsync(2, new CancellationToken(true)));
    }

    [Fact]
    public void SummariesRetainOutliersAndUnknowns()
    {
        var metric = new Metric("test", "ms", "fixture",
            [..Enumerable.Repeat(1d, 18), 100, 1000], null, 50);
        Assert.Equal(1, metric.P50);
        Assert.Equal(100, metric.P95);
        Assert.False(metric.WithinTarget);
        var unknown = new Metric("test", "ms", "fixture", [], "unavailable", 50);
        Assert.Null(unknown.WithinTarget);
        Assert.Null(unknown.P95);
    }
}
