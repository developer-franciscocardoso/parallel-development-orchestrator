using System.Collections.Immutable;

namespace Pda.Providers;

public sealed record ProviderDefinition(string Id, Uri Endpoint,
    int MaxConcurrentRequests, int RequestsPerMinute, long TokensPerMinute,
    string? CredentialEnvironmentVariable = null);

public sealed record ModelDefinition(string Id, string ProviderId,
    ImmutableArray<string> RuntimeIds, int ContextTokens, int MaxOutputTokens);

public sealed record ResolvedModel(ProviderDefinition Provider,
    ModelDefinition Model);
