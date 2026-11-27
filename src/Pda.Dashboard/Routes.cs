using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Pda.Hosting;

namespace Pda.Dashboard;

internal sealed record Control(string Action, string Plan, string? Attempt,
    string? Reason, int? Concurrency);

internal static class Routes
{
    public static void Map(WebApplication app, OrchestratorHost host,
        DashboardSessions sessions)
    {
        app.MapGet("/api/projects", () => host.Store.Projects());
        app.MapGet("/api/plans", () => host.Store.Plans());
        app.MapGet("/api/plans/{plan}", (string plan) => host.Report(plan));
        app.MapGet("/api/doctor", () => host.Doctor());
        app.MapGet("/api/plans/{plan}/export", (HttpContext ctx, string plan) =>
        {
            var report = host.Export(plan);
            ctx.Response.Headers.ContentDisposition =
                "attachment; filename=\"pda-report.json\"";
            return Results.Text(report, "application/json");
        });
        app.MapPost("/api/control", async (HttpContext ctx) =>
        {
            if (!Access.MayWrite(ctx, ctx.Request.Headers["X-PDA-CSRF"]))
                return Results.StatusCode(403);
            if (!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
            var control = await ctx.Request.ReadFromJsonAsync<Control>();
            Apply(host, control ?? throw new ArgumentException("Missing action."));
            return Results.NoContent();
        });
        app.MapPost("/control", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.StatusCode(415);
            var form = await ctx.Request.ReadFormAsync();
            if (!Access.MayWrite(ctx, form["csrf"])) return Results.StatusCode(403);
            int? value = int.TryParse(form["concurrency"], out var parsed)
                ? parsed : null;
            var plan = form["plan"].ToString();
            Apply(host, new(form["action"].ToString(), plan, form["attempt"],
                form["reason"], value));
            return DashboardHost.Redirect(ctx, DashboardData.Link(
                form["section"].ToString(), plan));
        });
        var slots = new SemaphoreSlim(16);
        app.MapGet("/api/events", (HttpContext ctx) =>
            Events(ctx, host, sessions, slots));
    }

    private static void Apply(OrchestratorHost host, Control control)
    {
        if (!host.Store.Plans().Any(p => p.Id == control.Plan))
            throw new KeyNotFoundException("Unknown plan.");
        var attempt = control.Attempt ?? "";
        switch (control.Action)
        {
            case "pause": host.Pause(control.Plan); break;
            case "resume": host.Resume(control.Plan); break;
            case "cancel-plan": host.CancelPlan(control.Plan); break;
            case "cancel-worker": host.CancelAttempt(control.Plan, attempt); break;
            case "retry": host.Retry(control.Plan, attempt, control.Reason ?? ""); break;
            case "concurrency": host.SetConcurrency(control.Plan,
                control.Concurrency ?? throw new ArgumentException("Missing concurrency.")); break;
            default: throw new ArgumentException("Unknown action.");
        }
    }

    private static async Task Events(HttpContext ctx, OrchestratorHost host,
        DashboardSessions sessions, SemaphoreSlim slots)
    {
        var token = ctx.RequestAborted;
        var after = ctx.Request.Headers["Last-Event-ID"].ToString();
        if (after.Length == 0) after = ctx.Request.Query["after"].ToString();
        var cursor = host.EventCursor();
        if (after.Length != 0 && (!long.TryParse(after, NumberStyles.None,
            CultureInfo.InvariantCulture, out cursor) || cursor < 0 ||
            cursor > host.EventCursor()))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        if (!slots.Wait(0)) { ctx.Response.StatusCode = 429; return; }
        try
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("retry: 1500\n\n", token);
            await ctx.Response.Body.FlushAsync(token);
            for (var tick = 0; tick < 300; tick++)
            {
                if (ctx.Items[Access.SessionKey] is DashboardSession session &&
                    sessions.Find(session.Cookie) is null) break;
                var rows = host.Events(cursor, 64);
                foreach (var row in rows)
                {
                    var data = JsonSerializer.Serialize(new { row.Kind, row.PlanId,
                        row.TaskId, row.AttemptId, row.Timestamp });
                    await ctx.Response.WriteAsync(
                        $"id: {row.Sequence}\nevent: journal\ndata: {data}\n\n", token);
                    cursor = row.Sequence;
                }
                if (rows.Count == 0 && tick % 25 == 0)
                    await ctx.Response.WriteAsync(": heartbeat\n\n", token);
                await ctx.Response.Body.FlushAsync(token);
                await Task.Delay(400, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {}
        finally { slots.Release(); }
    }
}
