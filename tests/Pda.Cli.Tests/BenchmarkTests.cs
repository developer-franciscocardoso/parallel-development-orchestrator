using System.Text.Json;

namespace Pda.Cli.Tests;

public sealed class BenchmarkTests
{
    [Fact]
    public async Task RealNativeApphostAndAuthenticatedEventsReturnSamples()
    {
        if (!OperatingSystem.IsWindows()) return;
        var run = Directory.CreateTempSubdirectory("pda-bench-http-");
        try
        {
            var data = await Benchmark.MeasureFrontend(run.FullName, 1, default);
            Assert.Equal(4, data.Length);
            foreach (var metric in data)
            {
                Assert.True(Assert.Single(metric.SamplesMs) > 0);
                Assert.Equal(metric.P50Ms, metric.P95Ms);
            }
            Assert.Contains(data, m =>
                m.Operation == "dashboard.journalWriteToSseDelivery");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            run.Delete(true);
        }
    }

    [Fact]
    public async Task InvalidIterationsFailBeforeCollectingSamples()
    {
        var home = Directory.CreateTempSubdirectory("pda-bench-options-");
        var error = new StringWriter();
        try
        {
            var code = await CliApplication.RunAsync(["--home", home.FullName,
                "--json", "benchmark", "--iterations", "0"], TextWriter.Null,
                error);
            Assert.Equal(2, code);
            using var report = JsonDocument.Parse(error.ToString());
            Assert.Equal("usage", report.RootElement.GetProperty("error").GetString());
            Assert.False(Directory.Exists(Path.Combine(home.FullName, "benchmarks")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            home.Delete(true);
        }
    }
}
