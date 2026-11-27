using System.Globalization;

namespace Pda.Cli;

internal sealed class UsageError(string message) : Exception(message);

internal sealed record CommandSpec(string Name, int Positionals,
    string Syntax, params string[] Options);

internal sealed class Arguments
{
    internal static readonly CommandSpec[] Commands =
    [
        new("init", 0, "init"),
        new("doctor", 0, "doctor"),
        new("project add", 2, "project add PROJECT REPOSITORY"),
        new("project inspect", 1, "project inspect PROJECT [--refresh]", "refresh"),
        new("plan create", 2, "plan create PROJECT PLAN --budget N " +
            "(--tasks FILE | --objective TEXT [--owned PATH] " +
            "[--runtime ID] [--checks FILE])",
            "budget", "tasks", "objective", "owned", "runtime", "checks"),
        new("plan validate", 1, "plan validate PLAN"),
        new("plan execute", 1, "plan execute PLAN"),
        new("task list", 1, "task list PLAN"),
        new("task inspect", 2, "task inspect PLAN TASK"),
        new("task retry", 2, "task retry PLAN TASK --reason TEXT " +
            "[--tokens N --evidence TEXT]", "reason", "tokens", "evidence"),
        new("task cancel", 2, "task cancel PLAN TASK"),
        new("worker list", 1, "worker list PLAN"),
        new("worker inspect", 2, "worker inspect PLAN ATTEMPT"),
        new("worker stop", 2, "worker stop PLAN ATTEMPT"),
        new("execution status", 1, "execution status PLAN"),
        new("execution recover", 1, "execution recover PLAN"),
        new("execution history", 1, "execution history PLAN [--after SEQ] " +
            "[--limit N]", "after", "limit"),
        new("review list", 1, "review list PLAN"),
        new("integration list", 1, "integration list PLAN"),
        new("dashboard", 0, "dashboard [--port N]", "port"),
        new("benchmark", 0, "benchmark [--iterations N] [--output FILE]",
            "iterations", "output"),
        new("update", 0, "update [--package FILE] [--sha256 HEX] " +
            "[--install-dir DIR]", "package", "sha256", "install-dir")
    ];

    internal string Name { get; private init; } = "";
    internal string[] Values { get; private init; } = [];
    private readonly Dictionary<string, List<string>> options = [];
    private static readonly HashSet<string> Flags = ["help", "json", "refresh"];

    internal bool Has(string name) => options.ContainsKey(name);
    internal string[] Many(string name) =>
        options.TryGetValue(name, out var values) ? [.. values] : [];
    internal string? Option(string name) =>
        options.TryGetValue(name, out var values) ? values.Single() : null;
    internal string Required(string name) => Option(name) ??
        throw new UsageError("Missing --" + name + ".");

    internal long Number(string name, long minimum, long maximum,
        long? fallback = null)
    {
        var value = Option(name);
        if (value == null && fallback.HasValue) return fallback.Value;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var result) || result < minimum || result > maximum)
            throw new UsageError($"--{name} requires {minimum} to {maximum}.");
        return result;
    }

    internal static Arguments Parse(string[] args)
    {
        var options = new Dictionary<string, List<string>>();
        var tokens = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                tokens.Add(token);
                continue;
            }
            var name = token[2..];
            if (!Flags.Contains(name) && !Commands.Any(c =>
                c.Options.Contains(name)) && name != "home")
                throw new UsageError("Unknown option " + token + ".");
            var value = Flags.Contains(name) ? "" :
                i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] :
                throw new UsageError(token + " requires a value.");
            if (!options.TryGetValue(name, out var list)) options[name] = list = [];
            else if (name != "owned") throw new UsageError("Duplicate " + token + ".");
            list.Add(value);
        }
        if (tokens.Count == 0 && !options.ContainsKey("help"))
            throw new UsageError("A command is required. Use --help.");
        var nameParts = tokens.Count > 0 && Commands.Any(c => c.Name == tokens[0])
            ? 1 : Math.Min(2, tokens.Count);
        var nameText = string.Join(' ', tokens.Take(nameParts));
        var spec = Commands.SingleOrDefault(c => c.Name == nameText);
        if (spec == null && nameText.Length > 0)
            throw new UsageError("Unknown command " + nameText + ".");
        var result = new Arguments { Name = nameText,
            Values = [..tokens.Skip(nameParts)] };
        foreach (var option in options) result.options.Add(option.Key, option.Value);
        if (result.Has("help")) return result;
        if (result.Values.Length != spec!.Positionals)
            throw new UsageError("Usage: pda " + spec.Syntax);
        foreach (var key in options.Keys.Except(["home", "json", "help"]))
            if (!spec.Options.Contains(key))
                throw new UsageError("--" + key + " is not valid for " + spec.Name);
        return result;
    }
}
