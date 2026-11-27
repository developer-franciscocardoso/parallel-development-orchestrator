using Pda.Core;
using Pda.Repositories;

namespace Pda.Planning;

public sealed class ProjectDiscovery
{
    private static readonly HashSet<string> Ignored = new(
        ["node_modules", "bin", "obj", "vendor", "target", ".venv", "dist",
         "build", ".execution", "__pycache__"], StringComparer.OrdinalIgnoreCase);

    public ProjectProfile Inspect(string repository)
    {
        var snapshot = new GitRepositoryService().TrackedFiles(repository);
        var findings = new Findings();
        long textBytes = 0;
        var manifestCount = 0;
        foreach (var file in snapshot.Files)
        {

            var kind = Kind(file.Path);
            if (kind == "other" || kind != "instruction" &&
                file.Path.Split('/').SkipLast(1).Any(Ignored.Contains)) continue;
            var evidence = new SourceEvidence(file.Path, kind, file.ObjectId,
                file.Bytes);
            if (kind is "manifest" or "instruction")
            {
                ContractValidator.ValidatePath(file.Path);
                var read = ++manifestCount <= 256 && file.Bytes <= 262144 &&
                    textBytes + file.Bytes <= 4 * 1024 * 1024;
                if (!read && kind == "instruction")
                    throw new InvalidDataException("Instruction exceeds discovery bounds: " + file.Path);
                if (!read) findings.Warnings.Add("Skipped bounded manifest: " + file.Path);
                else
                {
                    textBytes += file.Bytes;
                    var text = snapshot.ReadText(file);
                    if (kind == "instruction") evidence = evidence with { Content = text };
                    else ManifestAnalysis.Read(file.Path, text, findings);
                }
            }
            if (Language(file.Path) is { } language) findings.Languages.Add(language);
            if (file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                findings.Databases.Add("SQL (engine unspecified)");
            findings.Evidence.Add(evidence);
        }
        return new(snapshot.Repository.Root, snapshot.Repository.CommonDirectory,
            snapshot.Commit, DateTimeOffset.UtcNow, [.. findings.Languages],
            [.. findings.Frameworks], [.. findings.Build], [.. findings.Databases],
            [.. findings.Evidence], [.. findings.Checks], [.. findings.Warnings]);
    }

    private static string Kind(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "agents.md" or "agents.override.md" or "claude.md" ||
            path.Equals(".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".github/instructions/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".cursor/rules/", StringComparison.OrdinalIgnoreCase))
            return "instruction";
        if (name is "package.json" or "pyproject.toml" or "cargo.toml" or "go.mod" ||
            name.EndsWith(".csproj", StringComparison.Ordinal)) return "manifest";
        if (path.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
            name is ".gitlab-ci.yml" or "azure-pipelines.yml" or "jenkinsfile")
            return "ci";
        if (name.StartsWith("readme", StringComparison.Ordinal) ||
            path.StartsWith("docs/", StringComparison.Ordinal)) return "documentation";
        return Language(path) != null ? "source" : "other";
    }

    private static string? Language(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "C#", ".js" or ".jsx" or ".mjs" or ".cjs" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript", ".py" => "Python", ".rs" => "Rust",
            ".go" => "Go", ".ps1" or ".psm1" => "PowerShell", ".java" => "Java",
            ".rb" => "Ruby", ".php" => "PHP", ".sql" => "SQL",
            ".cpp" or ".hpp" => "C++", ".c" => "C", _ => null
        };
}

internal sealed class Findings
{
    internal SortedSet<string> Languages { get; } = new(StringComparer.Ordinal);
    internal SortedSet<string> Frameworks { get; } = new(StringComparer.Ordinal);
    internal SortedSet<string> Build { get; } = new(StringComparer.Ordinal);
    internal SortedSet<string> Databases { get; } = new(StringComparer.Ordinal);
    internal List<SourceEvidence> Evidence { get; } = [];
    internal List<ProjectCheck> Checks { get; } = [];
    internal List<string> Warnings { get; } = [];
}
