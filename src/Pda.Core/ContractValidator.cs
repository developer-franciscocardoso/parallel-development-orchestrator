using System.Text.RegularExpressions;

namespace Pda.Core;

public static class ContractValidator
{
    public static void Validate(IReadOnlyList<TaskContract> tasks)
    {
        Require(tasks is { Count: > 0 }, "A plan needs tasks.");
        var indexed = new Dictionary<string, TaskContract>(StringComparer.Ordinal);
        foreach (var task in tasks!)
        {
            ArgumentNullException.ThrowIfNull(task);
            ValidateTask(task);
            Require(indexed.TryAdd(task.Id, task), "Duplicate task ID.");
        }
        var ancestors = new Dictionary<string, HashSet<string>>();
        var pending = new HashSet<string>();
        foreach (var task in tasks)
            Visit(task.Id, indexed, ancestors, pending);
        foreach (var a in tasks)
            foreach (var b in tasks.Where(b => string.CompareOrdinal(a.Id, b.Id) < 0))
                ValidateOwnership(a, b, ancestors);
    }

    private static HashSet<string> Visit(string id,
        Dictionary<string, TaskContract> tasks,
        Dictionary<string, HashSet<string>> done, HashSet<string> pending)
    {
        if (done.TryGetValue(id, out var prior)) return prior;
        Require(tasks.ContainsKey(id), "Missing dependency: " + id);
        Require(pending.Add(id), "Dependency cycle: " + id);
        var result = new HashSet<string>(tasks[id].Dependencies);
        foreach (var dep in tasks[id].Dependencies)
            result.UnionWith(Visit(dep, tasks, done, pending));
        pending.Remove(id);
        return done[id] = result;
    }

    private static void ValidateOwnership(TaskContract a, TaskContract b,
        Dictionary<string, HashSet<string>> ancestors)
    {
        if (ancestors[a.Id].Contains(b.Id) || ancestors[b.Id].Contains(a.Id))
            return;
        Require(!Conflicts(a, b), $"Ownership conflict between {a.Id} and {b.Id}.");
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
        Require(!string.IsNullOrWhiteSpace(path), "Empty path.");
        foreach (var part in path.Split('/'))
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
        Require(!string.IsNullOrWhiteSpace(t.Objective), "Missing objective.");
        Require(t.Owned is { Length: > 0 } && t.ReadOnly != null &&
            t.Excluded != null && t.Dependencies != null, "Missing scope.");
        foreach (var p in t.Owned!.Concat(t.ReadOnly!).Concat(t.Excluded!))
            ValidatePath(p);
        foreach (var id in t.Dependencies!) ValidateId(id);
        Require(t.Dependencies!.Distinct().Count() == t.Dependencies.Length,
            "Duplicate dependency.");
        Require(t.Criteria is { Length: > 0 } && t.Deliverables is { Length: > 0 }
            && !t.Criteria.Concat(t.Deliverables).Any(string.IsNullOrWhiteSpace),
            "Missing criteria or deliverables.");
        Require(t.Verification is { Length: > 0 }, "Missing verification.");
        foreach (var command in t.Verification!)
            Require(command != null && !string.IsNullOrWhiteSpace(command.Executable)
                && !command.Executable.Any(char.IsControl)
                && command.Arguments != null && command.Arguments.All(a =>
                    a != null && !a.Contains('\0')), "Invalid command.");
        Require(Enum.IsDefined(t.Risk) && t.MaxAttempts > 0 &&
            t.TimeoutSeconds > 0 && t.TokenReservation >= 0, "Invalid limits.");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new ArgumentException(message);
    }
}
