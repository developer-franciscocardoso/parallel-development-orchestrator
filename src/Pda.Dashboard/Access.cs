using System.Net;
using Microsoft.AspNetCore.Http;

namespace Pda.Dashboard;

internal static class Access
{
    internal const string Cookie = "pda_dashboard";
    internal const string SessionKey = "pda.session";
    internal const string BearerKey = "pda.bearer";

    public static async Task Guard(HttpContext ctx, DashboardSessions sessions,
        Func<Task> next)
    {
        var request = ctx.Request;
        var connection = ctx.Connection;
        var headers = ctx.Response.Headers;
        headers.CacheControl = "no-store";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "default-src 'none'; " +
            "style-src 'self'; script-src 'self'; connect-src 'self'; " +
            "img-src 'self'; base-uri 'none'; frame-ancestors 'none'; " +
            "form-action 'self'";
        if (connection.RemoteIpAddress is not { } address ||
            !IPAddress.IsLoopback(address) || request.Host.Host != "127.0.0.1" ||
            (request.Host.Port ?? 80) != connection.LocalPort ||
            request.Scheme != "http")
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != "http://" + request.Host)
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        if (request.Path == "/login" || request.Path == "/dashboard.css" ||
            request.Path == "/favicon.ico")
        {
            await next();
            return;
        }
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            sessions.Accepts(auth[7..]))
            ctx.Items[BearerKey] = true;
        var session = sessions.Find(request.Cookies[Cookie]);
        if (session is not null) ctx.Items[SessionKey] = session;
        if (session is null && !ctx.Items.ContainsKey(BearerKey))
        {
            if (request.Method == "GET" && request.Path == "/")
                ctx.Response.Redirect("/login");
            else ctx.Response.StatusCode = 401;
            return;
        }
        await next();
    }

    internal static bool SameOrigin(HttpRequest request) =>
        request.Headers.Origin.ToString() == "http://" + request.Host;

    internal static bool MayWrite(HttpContext ctx, string? csrf)
    {
        if (ctx.Items.ContainsKey(BearerKey)) return true;
        return SameOrigin(ctx.Request) &&
            ctx.Items[SessionKey] is DashboardSession session &&
            csrf == session.Csrf;
    }
}
