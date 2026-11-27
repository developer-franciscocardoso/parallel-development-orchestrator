namespace Pda.Providers.Tests;

public sealed class ProviderTests : IDisposable
{
    private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory();

    private const string Sample = """
        {
          "providers": [{
            "id": "lab", "endpoint": "https://example.invalid/v1",
            "credentialEnvironmentVariable": "LAB_TOKEN",
            "maxConcurrentRequests": 2, "requestsPerMinute": 10,
            "tokensPerMinute": 5000
          }],
          "models": [{
            "id": "test-model", "providerId": "lab",
            "runtimeIds": ["sim"], "contextTokens": 256, "maxOutputTokens": 64
          }]
        }
        """;

    private ProviderRegistry Load(string data)
    {
        var file = Path.Combine(folder.FullName, "providers.json");
        File.WriteAllText(file, data);
        return ProviderRegistry.Load(file, ["sim", "codex"]);
    }

    [Fact]
    public void ResolvesOnlyCompatibleRegisteredModels()
    {
        var registry = Load(Sample);
        var resolved = registry.Resolve("sim", "test-model");
        Assert.Equal(2, resolved.Provider.MaxConcurrentRequests);
        Assert.Equal("LAB_TOKEN", resolved.Provider.CredentialEnvironmentVariable);
        Assert.Equal(64, resolved.Model.MaxOutputTokens);
        Assert.Throws<InvalidOperationException>(() =>
            registry.Resolve("codex", "test-model"));
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("sim", "absent"));
    }

    [Theory]
    [InlineData("https://example.invalid/v1", "http://example.invalid")]
    [InlineData("https://example.invalid/v1", "https://user:key@example.invalid")]
    [InlineData("https://example.invalid/v1", "https://example.invalid/?key=value")]
    [InlineData("LAB_TOKEN", "literal key!")]
    [InlineData("\"providerId\": \"lab\"", "\"providerId\": \"absent\"")]
    [InlineData("[\"sim\"]", "[\"absent\"]")]
    [InlineData("[\"sim\"]", "[\"sim\",\"sim\"]")]
    [InlineData("256", "16")]
    [InlineData("5000", "0")]
    [InlineData("\"id\": \"lab\"", "\"id\": \"lab\", \"apiKey\": \"secret\"")]
    [InlineData("\"id\": \"lab\"", "\"id\": \"other\", \"id\": \"lab\"")]
    public void RejectsInvalidConfiguration(string oldValue, string newValue) =>
        Assert.Throws<InvalidDataException>(() =>
            Load(Sample.Replace(oldValue, newValue)));

    [Theory]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://127.0.0.2:8080/v1")]
    [InlineData("http://[::1]:8080/v1")]
    public void AcceptsLocalLoopbackHttp(string endpoint) =>
        Assert.Single(Load(Sample.Replace("https://example.invalid/v1",
            endpoint)).Providers);

    [Theory]
    [InlineData("providers")]
    [InlineData("models")]
    public void RejectsDuplicateIdentifiers(string section)
    {
        var config = System.Text.Json.Nodes.JsonNode.Parse(Sample)!;
        var entries = config[section]!.AsArray();
        entries.Add(entries[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => Load(config.ToJsonString()));
    }

    [Fact]
    public void EnforcesByteLimit() =>
        Assert.Throws<InvalidDataException>(() =>
            Load(Sample + new string(' ', 1024 * 1024)));

    public void Dispose() => folder.Delete(true);
}
