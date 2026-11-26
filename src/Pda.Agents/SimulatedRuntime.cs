using System.Text.Json;

namespace Pda.Agents;

public enum SimulationScenario { Success, Crash, Timeout, Cancelled, UnknownUsage }

public sealed class SimulatedRuntime(SimulationScenario scenario,
    Func<AgentRequest, CancellationToken, Task<WorkerReport>>? reportFactory
        = null) : IAgentRuntime
{
    public string Id => "simulated";

    public Task<RuntimeDiscovery> DiscoverAsync(AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeDiscovery(Id, "1", false,
            ["simulation", "events", "reports", "usage"], RuntimeError.None,
            "Deterministic local simulation."));
    }

    public async Task<RuntimeResult> RunAsync(AgentRequest request,
        AgentCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        Requests.Validate(request);
        using var outFile = File.Open(request.OutputLog, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(outFile) { AutoFlush = true };
        using var errors = File.Open(request.ErrorLog, FileMode.CreateNew);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, cancellationToken);
        var error = scenario switch
        {
            SimulationScenario.Crash => RuntimeError.Failed,
            SimulationScenario.Timeout => RuntimeError.Timeout,
            SimulationScenario.Cancelled => RuntimeError.Cancelled,
            _ => RuntimeError.None
        };
        Emit("simulation.started");
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (error != RuntimeError.None)
                return End(error, "Simulated " + error, null, null);
            var report = reportFactory == null
                ? new WorkerReport(request.TaskId, request.AttemptId,
                    "completed", "Simulated completion.", "", [], [], [])
                : await reportFactory(request, stop.Token).WaitAsync(stop.Token);
            report.Validate(request);
            report.Write(request.ReportPath);
            var usage = scenario == SimulationScenario.UnknownUsage
                ? null : new TokenUsage(10, 2, 3);
            return End(report.Status == "completed" ? RuntimeError.None
                : RuntimeError.Failed, "Simulated report.", usage, report);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return End(timeout.IsCancellationRequested ? RuntimeError.Timeout
                : RuntimeError.Cancelled, "Simulation stopped.", null, null);
        }

        RuntimeResult End(RuntimeError result, string detail,
            TokenUsage? usage, WorkerReport? report)
        {
            Emit("simulation." + result.ToString().ToLowerInvariant());
            return new(result, detail, usage, report, null, true);
        }

        void Emit(string type)
        {
            var payload = JsonSerializer.SerializeToElement(new {
                type, simulated = true, attempt_id = request.AttemptId
            });
            log.WriteLine(payload.GetRawText());
            callbacks.Event(new(type, payload));
        }
    }
}
