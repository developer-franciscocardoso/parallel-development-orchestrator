using System.Text.Json;
using Pda.Core;
using Pda.Hosting;
using Pda.Security;

namespace Pda.Hosting.Tests;

public sealed class HostTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private string Home => Path.Combine(root, "state");

    private OrchestratorHost Open() => OrchestratorHost.Open(Home);

    private void Plan(OrchestratorHost host, string plan)
    {
        var tree = Directory.CreateDirectory(Path.Combine(root, "project"));
        var task = new TaskContract("t", "example", [], ["example"], [], [],
            ["example"], [new("verify", [])], ["example"], Risk.Low,
            2, 30, 1, "simulated", 50);
        host.Store.CreatePlan("project", tree.FullName, plan, [task], 100);
    }

    [Fact]
    public void InitializationPersistsConfigAndOfflineDefault()
    {
        var host = Open();
        Assert.False(host.Doctor().LiveConfigured);
        Assert.Single(host.Providers.Models);
        File.WriteAllText(Path.Combine(Home, "host.json"),
            """{"defaultConcurrency":4}""");
        var next = Open();
        Assert.Equal(4, next.Settings.DefaultConcurrency);
        Assert.Equal("simulated", next.Providers.Models.Single().Id);
    }

    [Fact]
    public async Task ControlsPersistAndCancellationIsTerminal()
    {
        var host = Open();
        Plan(host, "plan");
        host.SetConcurrency("plan", 4);
        host.Pause("plan");
        Assert.True(Open().Report("plan").Status.Paused);
        var paused = await host.ExecuteAsync("plan");
        Assert.False(paused.Complete);
        host.Resume("plan");
        var before = host.EventCursor();
        host.CancelPlan("plan");
        var after = host.EventCursor();
        host.CancelPlan("plan");
        Assert.Equal(after, host.EventCursor());
        Assert.True(after > before);
        Assert.True(Open().Report("plan").Cancelled);
        Assert.Equal(4, Open().Concurrency("plan"));
        Assert.Throws<InvalidOperationException>(() => host.Resume("plan"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.ExecuteAsync("plan"));
    }

    [Fact]
    public void CursorReadsEveryPlanInBoundedPages()
    {
        var host = Open();
        Plan(host, "one");
        Plan(host, "two");
        var first = host.Events(0, 2);
        Assert.Equal(2, first.Count);
        var next = host.Events(first[^1].Sequence, 1);
        Assert.Single(next);
        Assert.Equal("two", next[0].PlanId);
        Assert.All(host.Events(0, 100, "one"), e => Assert.Equal("one", e.PlanId));
        Assert.Empty(host.Events(host.EventCursor(), 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Events(0, 101));
        using var export = JsonDocument.Parse(host.Export("one"));
        Assert.Equal("one", export.RootElement.GetProperty("plan")
            .GetProperty("id").GetString());
        Assert.Equal(0, export.RootElement.GetProperty("metrics")
            .GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void RefusesStateCreationWithinACheckout()
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.False(Directory.Exists(Home));
    }

    [Fact]
    public void InvalidConcurrencyAndDuplicateSettingsAreRejected()
    {
        var host = Open();
        Plan(host, "plan");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            host.SetConcurrency("plan", 7));
        File.WriteAllText(Path.Combine(Home, "host.json"),
            """{"enableLiveCodex":false,"enableLiveCodex":true}""");
        Assert.Throws<JsonException>(() => Open());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://chatgpt.com/backend-api/codex", true)]
    [InlineData("https://api.openai.com.evil.test/v1", false)]
    [InlineData("https://api.openai.com/v1?route=elsewhere", false)]
    public void LiveModelUsesExactApprovedRoute(string endpoint, bool allowed)
    {
        var profile = new CodexProfile("unused", "unused",
            OpenAiEndpoint: new(endpoint));
        if (!allowed)
        {
            Assert.Throws<InvalidDataException>(() =>
                CodexPolicy.ProviderOptions(profile));
            return;
        }
        var route = CodexPolicy.ProviderOptions(profile);
        Assert.Contains("model_provider=\"pda\"", route);
        Assert.Contains("base_url=\"" + endpoint + "\"", route[1]);
        Assert.Contains("requires_openai_auth=true", route[1]);
    }

    [Fact]
    public void CancelRemainsAvailableDuringSupervisedStartup()
    {
        var host = Open();
        Plan(host, "plan");
        using var startup = host.Store.AcquireStartupGuard();
        var reserved = host.Store.Reserve("plan", "t", "request")!;
        host.CancelPlan("plan");
        var stop = Assert.Single(host.Store.Artifacts<bool>("plan", "cancel"));
        Assert.Equal(reserved.Id, stop.Id);
        Assert.True(stop.Data);
    }

    [Theory]
    [InlineData("custom", "https://api.openai.com/v1", null)]
    [InlineData("openai", "https://example.org", null)]
    [InlineData("openai", "https://api.openai.com/v1", "EXTERNAL_KEY")]
    public void RejectsUnsupportedLiveRegistryAtStartup(string provider,
        string endpoint, string? variable)
    {
        Open();
        var file = Path.Combine(root, "trusted.exe");
        File.WriteAllText(file, "");
        File.WriteAllText(Path.Combine(Home, "host.json"), JsonSerializer.Serialize(
            new { enableLiveCodex = true, codexExecutable = file, codexModel = "model" }));
        var catalog = new
        {
            providers = new[] { new { id = provider, endpoint,
                maxConcurrentRequests = 2, requestsPerMinute = 30,
                tokensPerMinute = 1000, credentialEnvironmentVariable = variable } },
            models = new [] {"simulated", "model"}.Select(m => new
            {
                id = m, providerId = provider, contextTokens = 500, maxOutputTokens = 50,
                runtimeIds = new[] {m == "model" ? "codex" : "simulated"}
            })
        };
        File.WriteAllText(Path.Combine(Home, "providers.json"),
            JsonSerializer.Serialize(catalog));
        Assert.Throws<InvalidDataException>(() => Open());
    }
    public void Dispose() => Directory.Delete(root, true);
}
