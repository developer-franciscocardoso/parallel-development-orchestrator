using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Pda.Core;
using Pda.Dashboard;
using Pda.Hosting;

namespace Pda.Dashboard.Tests;

public sealed class DashboardApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "pda-web-" + Guid.NewGuid().ToString("N"));
    private readonly string secret = Guid.NewGuid().ToString("N");
    private OrchestratorHost host = null!;
    private WebApplication server = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        var repo = Directory.CreateDirectory(Path.Combine(directory, "repo"));
        host = OrchestratorHost.Open(Path.Combine(directory, "home"));
        var one = new TaskContract("T1", "Improve parser", [], ["src"], [],
            [], ["check"], [new("dotnet", ["--version"])], ["code"], Risk.Low,
            2, 60, 1, "simulated", 100);
        var two = one with { Id = "T2", Dependencies = ["T1"], Owned = ["ui"] };
        host.Store.CreatePlan("parser", repo.FullName, "alpha", [one, two], 500);
        server = DashboardHost.Create(host, 0, secret);
        await server.StartAsync();
        client = new(new HttpClientHandler { AllowAutoRedirect = false,
            UseProxy = false }) { BaseAddress = new Uri(server.Urls.Single()) };
    }

    [Fact]
    public async Task GuardsApiAndRejectsHostOrOriginSpoofing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,
            (await client.GetAsync("/")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Contains("alpha", await client.GetStringAsync("/api/plans"));
        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/api/plans");
        spoofed.Headers.Host = "untrusted.invalid";
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(spoofed)).StatusCode);
        using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/control");
        forged.Headers.Add("Origin", "https://untrusted.invalid");
        forged.Content = JsonContent.Create(new { action = "pause", plan = "alpha" });
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(forged)).StatusCode);
        Assert.False(host.Report("alpha").Status.Paused);
    }

    [Fact]
    public async Task BrowserSessionRendersAllSectionsAndGuardsWrites()
    {
        var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        client.DefaultRequestHeaders.Add("Origin", origin);
        using var login = await client.PostAsync("/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = secret }));
        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        var page = await client.GetStringAsync("/");
        var csrf = Regex.Match(page,
            "name=\"csrf\" value=\"([a-f0-9]{64})\"").Groups[1].Value;
        Assert.Equal(64, csrf.Length);
        Assert.DoesNotContain(secret, page);
        foreach (var section in DashboardData.Sections)
        {
            var result = await client.GetStringAsync(DashboardData.Link(
                section, "alpha"));
            Assert.Contains("<h1>" + section + "</h1>", result);
        }
        var pause = new { action = "pause", plan = "alpha" };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/control", pause)).StatusCode);
        client.DefaultRequestHeaders.Add("X-PDA-CSRF", csrf);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/control", pause)).StatusCode);
        Assert.True(host.Report("alpha").Status.Paused);
        using var logout = await client.PostAsync("/logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["csrf"] = csrf }));
        Assert.Equal(HttpStatusCode.SeeOther, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/plans")).StatusCode);
    }

    [Fact]
    public async Task EventStreamReplaysAfterLastEventIdAndExportsRealReport()
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", secret);
        var before = host.EventCursor();
        host.Pause("alpha");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/events?after=0");
        request.Headers.Add("Last-Event-ID", before.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
        } while (!line.StartsWith("id: ", StringComparison.Ordinal));
        Assert.True(long.Parse(line[4..]) > before);
        Assert.Equal("event: journal", await reader.ReadLineAsync(timeout.Token));
        Assert.Contains("alpha", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/events?after=-1")).StatusCode);
        var export = await client.GetStringAsync("/api/plans/alpha/export");
        Assert.Equal(host.Export("alpha"), export);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (server != null)
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
