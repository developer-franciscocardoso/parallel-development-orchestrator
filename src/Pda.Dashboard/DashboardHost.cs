using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pda.Hosting;

namespace Pda.Dashboard;

public static class DashboardHost
{
    public static async Task RunAsync(OrchestratorHost host, int port = 7412,
        CancellationToken token = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        var secret = Environment.GetEnvironmentVariable("PDA_DASHBOARD_TOKEN");
        await using var app = Create(host, port, secret ?? "");
        await app.StartAsync(token);
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {}
        finally { await app.StopAsync(CancellationToken.None); }
    }

    internal static WebApplication Create(OrchestratorHost host, int port,
        string secret)
    {
        var sessions = new DashboardSessions(secret);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, port);
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 16384;
            server.Limits.MaxConcurrentConnections = 128;
        });
        builder.Services.AddRazorComponents();
        var app = builder.Build();
        app.Use((ctx, next) => Access.Guard(ctx, sessions, next));
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception e) when (!ctx.Response.HasStarted &&
                e is ArgumentException or InvalidOperationException or
                KeyNotFoundException or InvalidDataException or
                System.Text.Json.JsonException or BadHttpRequestException)
            {
                ctx.Response.StatusCode = e is KeyNotFoundException ? 404 :
                    e is InvalidOperationException ? 409 : 400;
                await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
            }
        });
        app.MapGet("/dashboard.css", () => Asset("dashboard.css", "text/css"));
        app.MapGet("/dashboard.js", () =>
            Asset("dashboard.js", "text/javascript"));
        app.MapGet("/login", () => new RazorComponentResult<Login>());
        app.MapPost("/login", async (HttpContext ctx) =>
        {
            if (!Access.SameOrigin(ctx.Request) || !ctx.Request.HasFormContentType)
                return Results.StatusCode(403);
            var form = await ctx.Request.ReadFormAsync();
            if (!sessions.Accepts(form["token"].ToString()))
                return Results.Text("Invalid dashboard token.", statusCode: 401);
            var session = sessions.Open();
            ctx.Response.Cookies.Append(Access.Cookie, session.Cookie,
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict,
                    Path = "/", Expires = session.Expires, IsEssential = true });
            return Redirect(ctx, "/");
        });
        app.MapPost("/logout", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.StatusCode(415);
            var form = await ctx.Request.ReadFormAsync();
            if (!Access.MayWrite(ctx, form["csrf"])) return Results.StatusCode(403);
            if (ctx.Items[Access.SessionKey] is DashboardSession session)
                sessions.Close(session.Cookie);
            ctx.Response.Cookies.Delete(Access.Cookie, new CookieOptions { Path = "/" });
            return Redirect(ctx, "/login");
        });
        app.MapGet("/", (HttpContext ctx) =>
        {
            var data = DashboardData.Load(host, ctx.Request.Query);
            var csrf = (ctx.Items[Access.SessionKey] as DashboardSession)?.Csrf;
            return new RazorComponentResult<Page>(new { Data = data, Csrf = csrf ?? "" });
        });
        Routes.Map(app, host, sessions);
        return app;
    }

    internal static IResult Redirect(HttpContext ctx, string location)
    {
        ctx.Response.Headers.Location = location;
        return Results.StatusCode(303);
    }

    private static IResult Asset(string name, string type)
    {
        var resource = typeof(DashboardHost).Assembly.GetManifestResourceStream(
            "Pda.Dashboard.Assets." + name);
        return Results.Stream(resource ?? throw new FileNotFoundException(name),
            type + "; charset=utf-8");
    }
}
