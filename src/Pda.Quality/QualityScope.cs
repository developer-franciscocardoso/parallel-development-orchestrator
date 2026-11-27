using Pda.Core;

namespace Pda.Quality;

public static class QualityScope
{
    private static readonly HashSet<string> Protected = new(
        [".git", ".gitmodules", ".gitattributes", ".codex", ".agents",
         ".execution", "agents.md", "agents.override.md", "claude.md"],
        StringComparer.OrdinalIgnoreCase);

    public static bool Allows(TaskContract task, string path)
    {
        try { ContractValidator.ValidatePath(path); }
        catch (ArgumentException) { return false; }
        if (path.Split('/').Any(Protected.Contains)) return false;
        string[] controls = [".github/copilot-instructions.md",
            ".github/instructions", ".cursor/rules"];
        return !controls.Any(p => ContractValidator.ContainsPath(p, path)) &&
            task.Owned.Any(p => ContractValidator.ContainsPath(p, path)) &&
            !task.ReadOnly.Concat(task.Excluded).Any(p =>
                ContractValidator.ContainsPath(p, path) ||
                ContractValidator.ContainsPath(path, p));
    }
}
