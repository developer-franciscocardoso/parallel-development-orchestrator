using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pda.Benchmarks;

public sealed record BenchmarkEnvironment(string OperatingSystem, string Runtime,
    string Architecture, int LogicalProcessors);

public sealed record Metric(string Name, string Unit, string Method,
    IReadOnlyList<double> Samples, string? Unavailable, double? TargetP95 = null)
{
    public double? P50 => Percentile(0.50);
    public double? P95 => Percentile(0.95);
    public bool? WithinTarget => TargetP95.HasValue && P95.HasValue
        ? P95 <= TargetP95 : null;

    private double? Percentile(double rank) => Samples.Count == 0 ? null :
        Samples.Order().ElementAt((int)Math.Ceiling(rank * Samples.Count) - 1);
}

public sealed record BenchmarkReport(DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, int Iterations, BenchmarkEnvironment Environment,
    IReadOnlyList<Metric> Metrics);

internal sealed class Measurements
{
    private readonly Dictionary<string, Entry> entries = [];
    private sealed record Entry(string Unit, string Method, double? Target)
    {
        internal List<double> Values { get; } = [];
        internal string? Missing { get; set; }
    }

    internal void Define(string key, string unit, string method,
        double? target = null) => entries.Add(key, new(unit, method, target));

    internal void Record(string key, double value)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new InvalidDataException("Invalid measurement: " + key);
        entries[key].Values.Add(value);
    }

    internal void Missing(string key, string reason) => entries[key].Missing = reason;
    internal void Elapsed(string key, long start) =>
        Record(key, Stopwatch.GetElapsedTime(start).TotalMilliseconds);

    internal Metric[] Finish() => entries.Select(p => new Metric(p.Key,
        p.Value.Unit, p.Value.Method, p.Value.Values.ToArray(),
        p.Value.Values.Count == 0 ? p.Value.Missing ?? "Workload unavailable." :
        p.Value.Missing, p.Value.Target)).ToArray();
}
