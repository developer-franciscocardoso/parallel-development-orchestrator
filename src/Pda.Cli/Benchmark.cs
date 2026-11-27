using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Pda.Benchmarks;
using Pda.Execution;
using Pda.Hosting;
using Pda.Security;

namespace Pda.Cli;

internal static partial class CliBuiltins
{
    static partial void RegisterBenchmark(ref CliFeature? feature) =>
        feature = Benchmark.Run;
}

internal sealed record Timing(string Operation, double[] SamplesMs,
    double P50Ms, double P95Ms, double? TargetMs, bool? MeetsTarget)
{
    internal static Timing From(string operation, List<double> samples,
        double? target = null)
    {
        var ordered = samples.Order().ToArray();
        double Percentile(double fraction) =>
            ordered[(int)Math.Ceiling(fraction * ordered.Length) - 1];
        var p95 = Percentile(0.95);
        return new(operation, samples.ToArray(), Percentile(0.50), p95, target,
            target.HasValue ? p95 < target.Value : null);
    }
}

internal static class Benchmark
{
    internal static async Task<object> Run(OrchestratorHost caller,
        IReadOnlyDictionary<string, string> options, CancellationToken token)
    {
        var value = options.GetValueOrDefault("iterations", "3");
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var count) || count is < 1 or > 20)
            throw new UsageError("--iterations requires an integer from 1 to 20.");
        var output = options.TryGetValue("output", out var filename)
            ? Path.GetFullPath(filename) : null;
        if (output != null && Path.Exists(output))
            throw new IOException("Benchmark output already exists.");
        if (output != null) TrustedPaths.Directory(Path.GetDirectoryName(output)!);
        var root = Path.Combine(caller.Home, "benchmarks");
        Directory.CreateDirectory(root);
        TrustedPaths.Directory(root);
        var run = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        TrustedPaths.Directory(run);
        var started = DateTimeOffset.UtcNow;
        var frontend = await MeasureFrontend(run, count, token);
        await using (var raw = new FileStream(Path.Combine(run, "frontend.json"),
            FileMode.CreateNew))
            await JsonSerializer.SerializeAsync(raw, frontend,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
        var engine = options.ContainsKey("frontend-only") ? null
            : await Runner.RunAsync(count, token);
        var report = new {
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
            iterations = count, evidence = run, engine, frontend,
            environment = new {
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                apphost = Executable(), clockFrequency = Stopwatch.Frequency
            }
        };
        if (output != null)
        {
            var staging = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(staging, FileMode.CreateNew))
                    await JsonSerializer.SerializeAsync(file, report,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web) {
                            WriteIndented = true }, token);
                File.Move(staging, output, false);
            }
            finally { File.Delete(staging); }
        }
        return report;
    }

    private static string Executable() => TrustedPaths.File(Path.Combine(
        AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "pda.exe" : "pda"));

    private static Dictionary<string, string> ChildEnvironment()
    {
        string[] names = ["SystemRoot", "SystemDrive", "windir", "PATH",
            "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT_X64", "ProgramFiles",
            "ProgramFiles(x86)", "ProgramW6432", "TEMP", "TMP", "USERPROFILE",
            "LOCALAPPDATA", "APPDATA"];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (Environment.GetEnvironmentVariable(name) is string value)
                values[name] = value;
        return values;
    }

    internal static async Task<Timing[]> MeasureFrontend(string run, int count,
        CancellationToken token)
    {
        var dir = Path.Combine(run, "isolated-home");
        var host = OrchestratorHost.Open(dir);
        List<double> help = [], doctor = [], requests = [], events = [];
        var executable = Executable();
        for (var i = 0; i < count; i++)
        {
            help.Add(await Launch(executable, dir, "--help", token));
            doctor.Add(await Launch(executable, dir, "doctor", token));
        }
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var environment = ChildEnvironment();
        environment["PDA_DASHBOARD_TOKEN"] = secret;
        var request = new ProcessRequest(executable,
            ["--home", dir, "dashboard", "--port", port.ToString(
                CultureInfo.InvariantCulture)], run, Path.Combine(run, "server.out"),
            Path.Combine(run, "server.err"), TimeSpan.FromMinutes(5),
            Environment: environment);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var supervisor = new ProcessSupervisor();
        var daemon = supervisor.RunAsync(request, (_, _) => Task.CompletedTask,
            stop.Token);
        try
        {
            using var handler = new HttpClientHandler {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false
            };
            using var client = new HttpClient(handler) {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", secret);
            await WaitForServer(client, daemon, token);
            for (var i = 0; i < count; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var bytes = await client.GetByteArrayAsync("/api/projects", token);
                requests.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Unexpected dashboard response.");
                events.Add(await EventLatency(client, host, run, i, token));
            }
        }
        finally
        {
            await stop.CancelAsync();
            var outcome = await daemon;
            if (!outcome.TreeStopped)
                throw new InvalidOperationException("Dashboard process tree unresolved.");
        }
        return [
            Timing.From("native.help.processStartToExit", help, 1000),
            Timing.From("native.doctor.processStartToExit", doctor),
            Timing.From("http.projects.authenticatedWarmRequest", requests, 100),
            Timing.From("dashboard.journalWriteToSseDelivery", events, 1000)
        ];
    }

    private static async Task<double> Launch(string executable, string home,
        string operation, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var pair in ChildEnvironment())
            start.Environment[pair.Key] = pair.Value;
        foreach (var arg in new[] {"--home", home, "--json", operation})
            start.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var at = Stopwatch.GetTimestamp();
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Native apphost failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var elapsed = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Native benchmark failed: " + error);
        using var json = JsonDocument.Parse(output);
        return elapsed;
    }

    private static async Task WaitForServer(HttpClient client,
        Task<ProcessOutcome> daemon, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!daemon.IsCompleted)
        {
            try
            {
                using var reply = await client.GetAsync("/api/projects",
                    deadline.Token);
                reply.EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException e) when (e.StatusCode == null)
            { await Task.Delay(75, deadline.Token); }
        }
        var outcome = await daemon;
        throw new IOException("Dashboard failed to start, exit " + outcome.ExitCode);
    }

    private static async Task<double> EventLatency(HttpClient client,
        OrchestratorHost host, string run, int index, CancellationToken token)
    {
        var after = host.EventCursor();
        var uri = "/api/events?after=" + after.ToString(CultureInfo.InvariantCulture);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var lines = new StreamReader(stream);
        if (await lines.ReadLineAsync(deadline.Token) != "retry: 1500")
            throw new InvalidDataException("Missing SSE stream handshake.");
        var target = Directory.CreateDirectory(Path.Combine(run, "project-" + index));
        var started = Stopwatch.GetTimestamp();
        host.Store.RegisterProject("sample-" + index, target.FullName);
        var wanted = host.EventCursor();
        var eventId = "";
        while (await lines.ReadLineAsync(deadline.Token) is string line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                eventId = line[4..];
            if (!line.StartsWith("data: ", StringComparison.Ordinal) ||
                eventId != wanted.ToString(CultureInfo.InvariantCulture)) continue;
            using var payload = JsonDocument.Parse(line[6..]);
            if (payload.RootElement.GetProperty("Kind").GetString() == "ProjectRegistered")
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        throw new EndOfStreamException("Journal event not received.");
    }
}
