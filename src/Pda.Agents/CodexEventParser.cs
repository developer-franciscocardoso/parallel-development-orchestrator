using System.Text.Json;

namespace Pda.Agents;

public sealed class CodexEventParser
{
    private bool invalid, active, completed;
    private long input, cached, output;

    public TokenUsage? Usage => completed && !invalid && !active
        ? new(input, cached, output) : null;

    public bool FailedTurn { get; private set; }
    public bool Malformed => invalid;
    public void Invalidate() => invalid = true;

    public AgentEvent? Accept(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString()
                ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(type) || DuplicateProperties(root))
                throw new JsonException();
            switch (type)
            {
                case "turn.started":
                    invalid |= active;
                    active = true;
                    break;
                case "turn.failed":
                case "error":
                    FailedTurn = invalid = true;
                    break;
                case "turn.completed":
                    Complete(root.GetProperty("usage"));
                    break;
            }
            return new(type, root.Clone());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException
            or FormatException or InvalidOperationException or OverflowException)
        {
            invalid = true;
            return null;
        }
    }

    private void Complete(JsonElement usage)
    {
        long nextInput = usage.GetProperty("input_tokens").GetInt64();
        long nextOutput = usage.GetProperty("output_tokens").GetInt64();
        long nextCached = usage.TryGetProperty("cached_input_tokens", out var c)
            ? c.GetInt64() : 0;
        invalid |= !active || nextInput < 0 || nextOutput < 0
            || nextCached < 0 || nextCached > nextInput
            || DuplicateProperties(usage);
        if (usage.TryGetProperty("total_tokens", out var declared))
            invalid |= declared.GetInt64() != checked(nextInput + nextOutput);
        input = checked(input + nextInput);
        output = checked(output + nextOutput);
        cached = checked(cached + nextCached);
        _ = checked(input + output);
        completed = true;
        active = false;
    }

    private static bool DuplicateProperties(JsonElement obj)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return obj.EnumerateObject().Any(p => !names.Add(p.Name));
    }
}
