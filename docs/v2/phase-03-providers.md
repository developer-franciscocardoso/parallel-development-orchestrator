# Provider and model configuration

ProviderRegistry.Load reads a trusted local UTF-8 JSON file. The host
supplies the identifiers of its installed runtime adapters. Keep this file
in host configuration outside worker-writable repositories. The registry
requires .NET 10 and has no runtime package dependencies.

The following example describes a local test provider and an illustrative
model. The host must supply the runtime ID shown in runtimeIds.

```json
{
  "providers": [{
    "id": "local",
    "endpoint": "http://127.0.0.1:8000/v1",
    "maxConcurrentRequests": 2,
    "requestsPerMinute": 30,
    "tokensPerMinute": 50000,
    "credentialEnvironmentVariable": "LOCAL_PROVIDER_TOKEN"
  }],
  "models": [{
    "id": "local/example",
    "providerId": "local",
    "runtimeIds": ["simulator"],
    "contextTokens": 8192,
    "maxOutputTokens": 1024
  }]
}
```

Provider and model identifiers are case-sensitive and unique within their
respective lists. Model identifiers are registry-wide so Resolve(runtimeId,
modelId) selects one provider unambiguously. It returns both definitions.
Compatibility and token capacities are administrator declarations. This
module makes no capability probe and sends no requests.

Endpoints must use HTTPS, or HTTP on localhost or a loopback IP literal.
User information, query strings and fragments are rejected. Configure
credentials outside this file. The optional field names an environment
variable. Loading the registry never reads its value.

Input is bounded to 1 MiB, 256 providers, 4,096 models and 64 runtime IDs
per model. Unknown fields, duplicate JSON properties, incompatible runtime
IDs, nonpositive limits and output capacity exceeding context are rejected.
The registry exposes immutable snapshots. Enforcement of observed rate and
usage limits belongs to scheduling.

Run `dotnet test tests/Pda.Providers.Tests -c Release` for the file parsing,
resolution, reference, limit and endpoint checks. Fixtures are synthetic
and require no credentials or network access.
