using System.Text.RegularExpressions;

namespace Pda.Core;

public static class ContractValidator
{
    public static void Validate(IReadOnlyList<TaskContract> tasks)
    {
        const int maxTasks = 256;
        Require(tasks is { Count: > 0 and <= maxTasks }, "Invalid task count.");
        var indexed = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = 0;
        long characters = 0;
        foreach (var task in tasks!)
        {
            ArgumentNullException.ThrowIfNull(task);
            ValidateTask(task);
            characters += task.Objective.Length + task.Id.Length + task.Runtime.Length;
            characters += task.Criteria.Concat(task.Deliverables).Concat(task.Owned)
                .Concat(task.ReadOnly).Concat(task.Excluded).Concat(task.Dependencies)
                .Sum(item => item.Length);
            characters += task.Verification.Sum(command =>
                command.Executable.Length + command.Arguments.Sum(a => a.Length));
            Require(characters <= 1_048_576, "Plan text limit exceeded.");
            paths += task.Owned.Length + task.ReadOnly.Length + task.Excluded.Length;
            Require(paths <= 2048, "Plan path limit exceeded.");
            Require(indexed.TryAdd(task.Id, indexed.Count), "Duplicate task ID.");
        }
        var ancestors = BuildAncestors(tasks, indexed);
        for (var a = 0; a < tasks.Count; a++)
        for (var b = a + 1; b < tasks.Count; b++)
        {
            if (ancestors[a][b] || ancestors[b][a]) continue;
            Require(!Conflicts(tasks[a], tasks[b]),
                $"Ownership conflict between {tasks[a].Id} and {tasks[b].Id}.");
        }
    }

    private static System.Collections.BitArray[] BuildAncestors(
        IReadOnlyList<TaskContract> tasks, Dictionary<string, int> index)
    {
        var count = tasks.Count;
        var incoming = tasks.Select(t => t.Dependencies.Length).ToArray();
        var children = tasks.Select(_ => new List<int>()).ToArray();
        var ancestors = tasks.Select(_ =>
            new System.Collections.BitArray(count)).ToArray();
        for (var task = 0; task < count; task++)
        foreach (var id in tasks[task].Dependencies)
        {
            Require(index.TryGetValue(id, out var parent),
                "Missing dependency: " + id);
            children[parent].Add(task);
        }
        var ready = new Queue<int>(Enumerable.Range(0, count)
            .Where(i => incoming[i] == 0));
        var visited = 0;
        while (ready.TryDequeue(out var parent))
        {
            visited++;
            foreach (var child in children[parent])
            {
                ancestors[child].Or(ancestors[parent]);
                ancestors[child][parent] = true;
                if (--incoming[child] == 0) ready.Enqueue(child);
            }
        }
        Require(visited == count, "Dependency cycle.");
        return ancestors;
    }
    public static bool Conflicts(TaskContract a, TaskContract b) =>
        WritesInto(a, b.Owned, b.Excluded.Concat(b.ReadOnly)) ||
        WritesInto(a, b.ReadOnly, []) || WritesInto(b, a.ReadOnly, []);

    private static bool WritesInto(TaskContract writer, string[] otherPaths,
        IEnumerable<string> otherExclusions)
    {
        foreach (var x in writer.Owned)
        foreach (var y in otherPaths.Where(y => Overlaps(x, y)))
        {
            var shared = x.Length > y.Length ? x : y;
            if (!writer.Excluded.Concat(writer.ReadOnly)
                .Concat(otherExclusions).Any(p => ContainsPath(p, shared)))
                return true;
        }
        return false;
    }
    public static bool ContainsPath(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(string a, string b) =>
        ContainsPath(a, b) || ContainsPath(b, a);

    public static void ValidateId(string id) =>
        Require(id is not null && Regex.IsMatch(id,
            @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z"), "Invalid ID.");

    public static void ValidatePath(string path)
    {
        Require(path is { Length: <= 1024 } && !string.IsNullOrWhiteSpace(path), "Empty path.");
        foreach (var part in path!.Split('/'))
            Require(part.Length > 0 && part.TrimEnd(' ', '.') == part &&
                !part.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c)) &&
                !Regex.IsMatch(part,
                    @"^(\.git(?:~\d+)?|git~\d+|con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                "Unsafe repository path: " + path);
    }

    private static void ValidateTask(TaskContract t)
    {
        ValidateId(t.Id);
        ValidateId(t.Runtime);
        Require(ValidText(t.Objective), "Missing objective.");
        Require(t.Owned is { Length: > 0 and <= 32 } && t.ReadOnly is { Length: <= 32 } &&
            t.Excluded is { Length: <= 32 } && t.Dependencies is { Length: <= 32 }, "Missing scope.");
        foreach (var p in t.Owned!.Concat(t.ReadOnly!).Concat(t.Excluded!))
            ValidatePath(p);
        foreach (var id in t.Dependencies!) ValidateId(id);
        Require(t.Dependencies!.Distinct().Count() == t.Dependencies.Length,
            "Duplicate dependency.");
        Require(t.Criteria is { Length: > 0 and <= 32 } && t.Deliverables is { Length: > 0 and <= 32 }
            && t.Criteria.Concat(t.Deliverables).All(ValidText),
            "Missing criteria or deliverables.");
        Require(t.Verification is { Length: > 0 and <= 16 }, "Missing verification.");
        foreach (var command in t.Verification!)
            Require(command != null && ValidText(command.Executable)
                && !command.Executable.Any(char.IsControl)
                && command.Arguments is { Length: <= 32 } && command.Arguments.All(a =>
                    a is { Length: <= 4096 } && !a.Contains('\0')), "Invalid command.");
        Require(Enum.IsDefined(t.Risk) && t.MaxAttempts > 0 &&
            t.TimeoutSeconds > 0 && t.TokenReservation >= 0, "Invalid limits.");
    }

    private static bool ValidText(string? value) =>
        value is { Length: <= 4096 } && !string.IsNullOrWhiteSpace(value);

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new ArgumentException(message);
    }
}
