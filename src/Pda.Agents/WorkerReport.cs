using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pda.Agents;

public sealed record WorkerTest(string Command, int ExitCode);
public sealed record WorkerReport(string TaskId, string AttemptId,
    string Status, string Summary, string Commit, string[] Criteria,
    WorkerTest[] Tests, string[] Risks)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    internal static WorkerReport Read(AgentRequest request)
    {
        using var stream = File.OpenRead(request.ReportPath);
        if (stream.Length > 1024 * 1024)
            throw new JsonException("Worker report exceeds size limit.");
        var report = JsonSerializer.Deserialize<WorkerReport>(stream, Format)
            ?? throw new JsonException("Worker report missing.");
        report.Validate(request);
        return report;
    }

    internal void Validate(AgentRequest request)
    {
        if (TaskId != request.TaskId || AttemptId != request.AttemptId
            || Status is not ("completed" or "blocked")
            || string.IsNullOrWhiteSpace(Summary)
            || Tests.Any(t => t == null || string.IsNullOrWhiteSpace(t.Command))
            || Criteria.Any(string.IsNullOrWhiteSpace)
            || Risks.Any(string.IsNullOrWhiteSpace))
            throw new JsonException("Invalid report identity or content.");
    }

    internal void Write(string path)
    {
        using var file = File.Open(path, FileMode.CreateNew);
        JsonSerializer.Serialize(file, this, Format);
    }

    internal static string SchemaPath => Path.Combine(
        AppContext.BaseDirectory, "pda-worker-report.schema.json");
}
