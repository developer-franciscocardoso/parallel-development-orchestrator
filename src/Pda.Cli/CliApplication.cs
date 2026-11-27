using System.Text.Json;
using System.Text.Json.Serialization;
using Pda.Core;
using Pda.Hosting;
using Pda.Planning;
using Pda.Quality;
using Pda.Recovery;
using Pda.Scheduling;
using Pda.Verification;

namespace Pda.Cli;

public static partial class CliApplication
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output,
        TextWriter error, CancellationToken token = default)
    {
        var compact = args.Contains("--json");
        try
        {
            var input = Arguments.Parse(args);
            if (input.Has("help"))
            {
                var commands = Arguments.Commands.Where(c =>
                    input.Name == "" || c.Name == input.Name).Select(c => c.Syntax);
                if (compact) Write(output, new { commands }, true);
                else output.WriteLine("pda [--home DIR] [--json] COMMAND" +
                    Environment.NewLine + string.Join(Environment.NewLine, commands));
                return 0;
            }
            var home = input.Option("home") ??
                Environment.GetEnvironmentVariable("PDA_HOME") ??
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "Pda");
            var host = OrchestratorHost.Open(Path.GetFullPath(home));
            var (code, data) = await Execute(host, input, token);
            Write(output, data, compact);
            return code;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Fail(130, "cancelled", "Operation cancelled."); }
        catch (UsageError e) { return Fail(2, "usage", e.Message); }
        catch (NotSupportedException e) { return Fail(3, "unavailable", e.Message); }
        catch (Exception e) { return Fail(1, "operation", e.Message); }

        int Fail(int code, string category, string message)
        {
            if (compact) Write(error, new { error = category, message }, true);
            else error.WriteLine(message);
            return code;
        }
    }

    private static void Write(TextWriter writer, object value, bool compact) =>
        writer.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(Json)
            { WriteIndented = !compact }));

    private static async Task<(int Code, object Data)> Execute(
        OrchestratorHost host, Arguments a, CancellationToken token)
    {
        var id = a.Values.ElementAtOrDefault(0) ?? "";
        var selector = a.Values.ElementAtOrDefault(1) ?? "";
        switch (a.Name)
        {
            case "init": return (0, new { home = host.Home, initialized = true });
            case "doctor":
                var diagnostics = host.Doctor();
                return (diagnostics.Ready ? 0 : 3, diagnostics);
            case "project add":
                return (0, Register(host, id, selector));
            case "project inspect":
                var project = host.Store.Projects().Single(p => p.Id == id);
                return (0, a.Has("refresh")
                    ? host.Planning.InspectAndRegister(id, project.Root)
                    : host.Store.LatestProjectProfile<ProjectProfile>(id)
                    ?? throw new InvalidOperationException("Inspect with --refresh."));
            case "plan create": return (0, Create(host, a));
            case "dashboard":
                await Pda.Dashboard.DashboardHost.RunAsync(host,
                    (int)a.Number("port", 1, 65535, 7412), token);
                return (0, new { stopped = true });
            case "benchmark":
            case "update":
                return (0, await CliBuiltins.Run(host, a, token));
        }
        if (!host.Store.Plans().Any(p => p.Id == id))
            throw new KeyNotFoundException("Unknown plan: " + id);
        switch (a.Name)
        {
            case "plan validate":
                var contracts = host.Store.Contracts(id);
                ContractValidator.Validate(contracts);
                return (0, new { plan = id, valid = true, tasks = contracts.Count });
            case "plan execute":
                var outcome = await host.ExecuteAsync(id, token);
                return (outcome.Complete ? 0 : 4, outcome);
            case "task list": return (0, host.Store.Contracts(id));
            case "task inspect": return (0, new {
                contract = host.Store.Contracts(id).Single(t => t.Id == selector),
                attempts = host.Store.Attempts(id).Where(t => t.TaskId == selector)
            });
            case "task retry": return (0, Retry(host, a));
            case "task cancel":
                var attempt = Latest(host, id, selector);
                host.CancelAttempt(id, attempt.Id);
                return (0, new { attempt.Id, cancellationRequested = true });
            case "worker list":
                return (0, host.Store.Artifacts<DispatchRecord>(id, "dispatch"));
            case "worker inspect": return (0, new {
                attempt = host.Store.Attempts(id).Single(w => w.Id == selector),
                dispatch = host.Store.Artifacts<DispatchRecord>(id, "dispatch")
                    .Single(w => w.Id == selector),
                process = host.Store.Artifacts<WorkerRecord>(id, "worker")
                    .SingleOrDefault(w => w.Id == selector)?.Data,
                simulation = host.Store.Artifacts<SimulationLease>(id, "simulation")
                    .SingleOrDefault(w => w.Id == selector)?.Data
            });
            case "worker stop":
                host.CancelAttempt(id, selector);
                return (0, new { attemptId = selector, cancellationRequested = true });
            case "execution status": return (0, host.Report(id));
            case "execution recover": return (0, host.Recovery.Recover(id));
            case "execution history": return (0, host.Events(
                a.Number("after", 0, long.MaxValue, 0),
                (int)a.Number("limit", 1, 100, 100), id));
            case "review list": return (0, host.Store.Artifacts<ReviewReport>(
                id, "review"));
            case "integration list": return (0, host.Store.Artifacts<IntegratedChange>(
                id, "integration"));
            default: throw new UsageError("Unrecognized command.");
        }
    }

    private static Attempt Latest(OrchestratorHost host, string plan, string task) =>
        host.Store.Attempts(plan).Where(a => a.TaskId == task)
            .MaxBy(a => a.Number) ?? throw new InvalidOperationException(
                "The task has no attempt.");

    private static Attempt Retry(OrchestratorHost host, Arguments a)
    {
        var plan = a.Values[0];
        var attempt = Latest(host, plan, a.Values[1]);
        var reason = a.Required("reason");
        if (a.Has("tokens") != a.Has("evidence"))
            throw new UsageError("Supply both --tokens and --evidence.");
        if (a.Has("tokens")) host.Recovery.Reconcile(plan, attempt.Id,
            a.Number("tokens", 0, long.MaxValue), a.Required("evidence"));
        return host.Retry(plan, attempt.Id, reason);
    }
}
