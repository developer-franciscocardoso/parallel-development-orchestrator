using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Pda.Core;

namespace Pda.Planning;

internal static class ManifestAnalysis
{
    internal static void Read(string path, string content, Findings findings)
    {
        var text = content.TrimStart('\ufeff');
        try
        {
            switch (Path.GetFileName(path).ToLowerInvariant())
            {
                case "package.json": ReadPackage(path, text, findings); break;
                case "go.mod":
                    if (Regex.IsMatch(text, @"(?m)^module\s+\S+"))
                        findings.Build.Add("Go modules");
                    break;
                case "cargo.toml":
                    if (Regex.IsMatch(text, @"(?m)^\[(package|workspace)\]\s*$"))
                        findings.Build.Add("Cargo");
                    break;
                case "pyproject.toml":
                    if (Regex.IsMatch(text, @"(?m)^\[build-system\]\s*$"))
                        findings.Build.Add("PEP 517");
                    break;
                default: ReadProject(path, text, findings); break;
            }
        }
        catch (Exception error) when (error is JsonException or XmlException
            or InvalidOperationException or InvalidDataException)
        {
            findings.Warnings.Add("Malformed manifest: " + path);
        }
    }

    private static void ReadPackage(string path, string content, Findings f)
    {
        using var doc = JsonDocument.Parse(content,
            new JsonDocumentOptions { MaxDepth = 24, AllowDuplicateProperties = false });
        var obj = doc.RootElement;
        var deps = new List<string>();
        foreach (var name in new[] { "dependencies", "devDependencies" })
            if (obj.TryGetProperty(name, out var group))
                deps.AddRange(group.EnumerateObject().Select(p => p.Name));
        string? script = null;
        if (obj.TryGetProperty("scripts", out var scripts) &&
            scripts.TryGetProperty("test", out var value)) script = value.GetString();
        var manager = obj.TryGetProperty("packageManager", out var declared)
            ? declared.GetString()?.Split('@')[0] : "npm";
        foreach (var dep in deps) Package(dep, f);
        if (manager is not ("npm" or "pnpm" or "yarn")) return;
        f.Build.Add(manager);
        if (script == null || !Regex.IsMatch(script,
            @"\A(?:vitest run|jest(?: --runInBand)?|mocha|node --test|playwright test)\z"))
            return;
        var scope = Scope(path);
        if (scope.Length > 0 && manager != "npm") return;
        var args = scope.Length == 0 ? new[] { "test" } :
            ["--prefix", "./" + scope, "test"];
        f.Checks.Add(new(path, scope, new(manager, args)));
    }

    private static void ReadProject(string path, string content, Findings f)
    {
        using var input = new StringReader(content);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 262144
        });
        var root = XElement.Load(reader);
        if (root.Name.LocalName != "Project") throw new InvalidDataException();
        f.Build.Add("MSBuild");
        var sdk = (string?)root.Attribute("Sdk");
        if (sdk == "Microsoft.NET.Sdk.Web") f.Frameworks.Add("ASP.NET Core");
        var packages = root.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include")).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dep in packages) Package(dep, f);
        if (packages.Contains("Microsoft.NET.Test.Sdk") &&
            packages.Overlaps(["xunit", "NUnit", "MSTest.TestFramework"]))
            f.Checks.Add(new(path, Scope(path), new("dotnet",
                ["test", "./" + path, "--no-restore"])));
    }

    private static string Scope(string path) =>
        path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";

    private static void Package(string dependency, Findings f)
    {
        switch (dependency.ToLowerInvariant())
        {
            case "react": f.Frameworks.Add("React"); break;
            case "vue": f.Frameworks.Add("Vue"); break;
            case "next": f.Frameworks.Add("Next.js"); break;
            case "xunit": f.Frameworks.Add("xUnit"); break;
            case "vitest": f.Frameworks.Add("Vitest"); break;
            case "jest": f.Frameworks.Add("Jest"); break;
            case "better-sqlite3": case "sqlite3": case "microsoft.data.sqlite":
                f.Databases.Add("SQLite"); break;
            case "pg": case "npgsql": f.Databases.Add("PostgreSQL"); break;
        }
    }
}
