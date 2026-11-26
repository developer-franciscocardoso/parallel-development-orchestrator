using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pda.Providers;

public sealed class ProviderRegistry
{
    private const int MaxBytes = 1024 * 1024;
    private readonly FrozenDictionary<string, ProviderDefinition> providers;
    private readonly FrozenDictionary<string, ModelDefinition> models;

    public IReadOnlyList<ProviderDefinition> Providers { get; }
    public IReadOnlyList<ModelDefinition> Models { get; }

    private sealed record Config(ImmutableArray<ProviderDefinition> Providers,
        ImmutableArray<ModelDefinition> Models);

    private static readonly JsonSerializerOptions Options = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    private ProviderRegistry(Config config, IEnumerable<string> runtimeIds)
    {
        Require(config.Providers.Length is > 0 and <= 256 &&
            config.Models.Length is > 0 and <= 4096, "Invalid registry size.");
        var runtimes = runtimeIds.ToHashSet(StringComparer.Ordinal);
        foreach (var provider in config.Providers) ValidateProvider(provider);
        Providers = config.Providers;
        providers = Index(Providers, p => p.Id);
        foreach (var model in config.Models) ValidateModel(model, runtimes);
        Models = config.Models;
        models = Index(Models, m => m.Id);
    }

    public static ProviderRegistry Load(string trustedPath,
        IEnumerable<string> runtimeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPath);
        ArgumentNullException.ThrowIfNull(runtimeIds);
        using var file = File.OpenRead(trustedPath);
        var length = file.Length;
        Require(length is > 0 and <= MaxBytes, "Invalid registry file size.");
        var bytes = new byte[(int)length];
        file.ReadExactly(bytes);
        Require(file.ReadByte() == -1, "Registry changed while reading.");
        var content = bytes.AsSpan();
        if (content.StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf]))
            content = content[3..];
        try
        {
            var config = JsonSerializer.Deserialize<Config>(content, Options)
                ?? throw new InvalidDataException("Missing registry.");
            return new ProviderRegistry(config, runtimeIds);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid provider registry JSON.");
        }
    }

    public ResolvedModel Resolve(string runtimeId, string modelId)
    {
        if (!models.TryGetValue(modelId, out var model))
            throw new KeyNotFoundException("Unknown model.");
        if (!model.RuntimeIds.Contains(runtimeId, StringComparer.Ordinal))
            throw new InvalidOperationException("Runtime does not support model.");
        return new(providers[model.ProviderId], model);
    }

    private static void ValidateProvider(ProviderDefinition provider)
    {
        Require(provider != null, "Missing provider definition.");
        ValidateId(provider!.Id);
        var uri = provider.Endpoint;
        Require(uri is { IsAbsoluteUri: true } && uri.Host.Length > 0 &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
            uri.Fragment.Length == 0 && (uri.Scheme == "https" ||
            uri.Scheme == "http" && LocalHost(uri)), "Invalid provider endpoint.");
        var variable = provider.CredentialEnvironmentVariable;
        Require(variable == null || Regex.IsMatch(variable,
            @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z"), "Invalid credential variable name.");
        Require(provider.MaxConcurrentRequests > 0 &&
            provider.RequestsPerMinute > 0 && provider.TokensPerMinute > 0,
            "Provider limits must be positive.");
    }

    private static bool LocalHost(Uri uri) =>
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(uri.IdnHost, out var ip) && IPAddress.IsLoopback(ip);

    private void ValidateModel(ModelDefinition model, HashSet<string> runtimes)
    {
        Require(model != null, "Missing model definition.");
        ValidateId(model!.Id);
        ValidateId(model.ProviderId);
        Require(providers.ContainsKey(model.ProviderId), "Unknown model provider.");
        Require(!model.RuntimeIds.IsDefaultOrEmpty && model.RuntimeIds.Length <= 64,
            "Invalid runtime list.");
        foreach (var id in model.RuntimeIds) ValidateId(id);
        Require(model.RuntimeIds.All(runtimes.Contains) &&
            model.RuntimeIds.Distinct().Count() == model.RuntimeIds.Length,
            "Unknown or duplicate model runtime.");
        Require(model.MaxOutputTokens > 0 &&
            model.ContextTokens >= model.MaxOutputTokens, "Invalid model capacity.");
    }

    private static FrozenDictionary<string, T> Index<T>(IReadOnlyList<T> values,
        Func<T, string> key)
    {
        var entries = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
            Require(entries.TryAdd(key(value), value), "Duplicate registry ID.");
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void ValidateId(string? id) => Require(id != null &&
        Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}\z"),
        "Invalid registry identifier.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
