using System.Text;
using System.Text.Json;
using Pda.Execution;
using Pda.Security;

namespace Pda.Agents.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private AgentRequest Request()
    {
        var tree = Directory.CreateDirectory(Path.Combine(root, "tree"));
        return new("task", "attempt", tree.FullName, Path.Combine(root, "out"),
            Path.Combine(root, "err"), Path.Combine(root, "report"), "work",
            TimeSpan.FromSeconds(20));
    }

    private static AgentCallbacks Callbacks(Action<AgentEvent>? publish = null)
        => new((_, _) => Task.CompletedTask, publish ?? (_ => { }));

    [Theory]
    [InlineData(SimulationScenario.Success, RuntimeError.None, true)]
    [InlineData(SimulationScenario.Crash, RuntimeError.Failed, false)]
    [InlineData(SimulationScenario.Timeout, RuntimeError.Timeout, false)]
    [InlineData(SimulationScenario.Cancelled, RuntimeError.Cancelled, false)]
    [InlineData(SimulationScenario.UnknownUsage, RuntimeError.None, false)]
    public async Task SimulatesLifecycle(SimulationScenario scenario,
        RuntimeError expected, bool known)
    {
        var runtime = new SimulatedRuntime(scenario);
        var request = Request();
        List<AgentEvent> events = [];
        var result = await runtime.RunAsync(request, Callbacks(events.Add));
        Assert.True(result.Simulated);
        Assert.Equal(expected, result.Error);
        Assert.Equal(known, result.Usage != null);
        Assert.Null(result.Process);
        Assert.Equal(2, events.Count);
        Assert.True(File.Exists(request.OutputLog));
        Assert.Equal(expected == RuntimeError.None, File.Exists(request.ReportPath));
    }

    [Fact]
    public async Task CancelledCallerHasNoFakeProcessIdentity()
    {
        var runtime = new SimulatedRuntime(SimulationScenario.Success);
        var result = await runtime.RunAsync(Request(), Callbacks(),
            new CancellationToken(true));
        Assert.Equal(RuntimeError.Cancelled, result.Error);
        Assert.Null(result.Process);
        Assert.Null(result.Usage);
    }

    [Fact]
    public void WorkerArgumentsSeparatePromptAndTrustedPolicy()
    {
        var request = Request() with { Model = "model-x",
            Prompt = "--trust --sandbox danger-full-access" };
        var options = CodexLaunch.Arguments(request, ["-c", "mock"]);
        Assert.Contains("--ignore-rules", options);
        Assert.Contains("--strict-config", options);
        Assert.Contains("--ignore-user-config", options);
        Assert.Contains("--ephemeral", options);
        Assert.DoesNotContain(request.Prompt, options);
        Assert.DoesNotContain("--trust", options);
        Assert.Equal("-", options[^1]);
    }

    [Theory]
    [InlineData("valid", RuntimeError.None, true)]
    [InlineData("wrong-attempt", RuntimeError.InvalidReport, true)]
    [InlineData("invalid-schema", RuntimeError.InvalidReport, true)]
    [InlineData("failed-turn", RuntimeError.Failed, false)]
    [InlineData("truncated", RuntimeError.Protocol, false)]
    public async Task SupervisedJsonlWorkerClassifiesResult(string mode,
        RuntimeError error, bool known)
    {
        var request = Request();
        var worker = Worker(request, mode);
        var result = await CodexRuntime.Execute(request, worker, Callbacks(),
            CancellationToken.None);
        Assert.Equal(error, result.Error);
        Assert.Equal(known, result.Usage != null);
        Assert.True(result.Process!.TreeStopped);
        Assert.False(result.Simulated);
    }

    [Fact]
    public async Task WholeTurnAtTruncatedLogBoundaryLeavesUsageUnknown()
    {
        var request = Request();
        string first = "{\"type\":\"turn.started\"}\r\n" +
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":2," +
            "\"output_tokens\":1}}\r\n";
        var worker = Worker(request, "extra-turn") with
            { MaxLogBytes = Encoding.UTF8.GetByteCount(first) };
        var result = await CodexRuntime.Execute(request, worker, Callbacks(),
            CancellationToken.None);
        Assert.Equal(first, File.ReadAllText(request.OutputLog));
        Assert.True(result.Process!.OutputTruncated);
        Assert.Equal(RuntimeError.Protocol, result.Error);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task PublishesSmallEventWhileWorkerStillRuns()
    {
        var request = Request();
        string marker = Path.Combine(root, "received");
        var result = await CodexRuntime.Execute(request, Worker(request, "wait"),
            Callbacks(evt =>
            {
                if (evt.Type == "turn.started") File.WriteAllText(marker, "ok");
            }), CancellationToken.None);
        Assert.Equal(RuntimeError.None, result.Error);
    }

    private ProcessRequest Worker(AgentRequest request, string mode)
    {
        var exe = Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        string result = JsonSerializer.Serialize(new
        {
            task_id = request.TaskId,
            attempt_id = mode == "wrong-attempt" ? "bad" : request.AttemptId,
            status = "completed", summary = "finished", commit = "abc",
            criteria = Array.Empty<string>(), tests = Array.Empty<string>(),
            risks = Array.Empty<string>()
        });
        if (mode == "invalid-schema") result = "{}";
        string marker = Path.Combine(root, "received").Replace("'", "''");
        string wait = mode == "wait" ? $$"""
            $t = [Diagnostics.Stopwatch]::StartNew()
            while (-not [IO.File]::Exists('{{marker}}')) {
                if ($t.ElapsedMilliseconds -gt 3000) {exit 19}
                [Threading.Thread]::Sleep(30)
            }
            """ : "";
        string type = mode == "failed-turn" ? "turn.failed" : "turn.completed";
        string write = mode == "truncated" ? "Write" : "WriteLine";
        string reportPath = request.ReportPath.Replace("'", "''");
        var script = $$$"""
            [Console]::WriteLine('{"type":"turn.started"}')
            {{{wait}}}
            [IO.File]::WriteAllText('{{{reportPath}}}', '{{{result}}}')
            [Console]::{{{write}}}('{"type":"{{{type}}}","usage":{"input_tokens":2,"output_tokens":1}}')
            """;
        if (mode == "extra-turn")
            script += "\n[Console]::WriteLine('{" + "\"type\":\"turn.started\"}')";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new(exe, ["-NoProfile", "-EncodedCommand", encoded],
            request.Worktree, request.OutputLog, request.ErrorLog, request.Timeout);
    }

    public void Dispose() => Directory.Delete(root, true);
}
