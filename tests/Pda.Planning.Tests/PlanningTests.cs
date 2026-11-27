using System.Diagnostics;
using Pda.Core;
using Pda.Persistence;

namespace Pda.Planning.Tests;

public sealed class PlanningTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory();
    private readonly string repo;
    private readonly StateStore state;
    private readonly PlanningService planner;

    public PlanningTests()
    {
        repo = scratch.CreateSubdirectory("repo").FullName;
        Git("init", "-q");
        Git("config", "user.email", "fixture@example.invalid");
        Git("config", "user.name", "Fixture");
        state = new(Path.Combine(scratch.FullName, "state.db"));
        planner = new(state);
    }

    private void Write(string name, string text)
    {
        var path = Path.Combine(repo, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repo,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output.Trim();
    }

    private void Commit()
    {
        Git("add", ".");
        Git("-c", "core.hooksPath=NUL", "commit", "-qm", "fixture");
    }

    [Fact]
    public void DiscoveryUsesCommittedMixedEvidenceAndVersionedProfiles()
    {
        Write("AGENTS.md", "Preserve these instructions.\n");
        Write("package.json", """
            {"scripts":{"test":"vitest run","build":"vite build"},
             "dependencies":{"react":"*","better-sqlite3":"*"}}
            """);
        Write("src/index.ts", "export const value = 1;");
        Write("src/other.py", "print(1)");
        Write("docs/setup.md", "Setup");
        Write(".github/workflows/build.yml", "name: Build");
        Commit();
        Write("AGENTS.md", "uncommitted replacement");
        var saved = planner.InspectAndRegister("project", repo);
        var p = saved.Data;
        Assert.Contains("TypeScript", p.Languages);
        Assert.Contains("Python", p.Languages);
        Assert.Contains("React", p.Frameworks);
        Assert.Contains("SQLite", p.Databases);
        Assert.Contains(p.Evidence, e => e.Kind == "instruction" &&
            e.Content?.TrimEnd() == "Preserve these instructions.");
        Assert.Contains(p.Evidence, e => e.Kind == "ci");
        Assert.Contains(p.Evidence, e => e.Kind == "documentation");
        Assert.Equal(Git("rev-parse", "HEAD"), p.Commit);
        var next = planner.InspectAndRegister("project", repo);
        Assert.Equal(2, next.Version);
        Assert.Empty(state.Plans());
        Assert.Equal(2, state.ProjectProfiles<ProjectProfile>("project").Count);
        var plan = planner.Generate("project", "plan", "Update app", ["src"],
            "sim", 100);
        Assert.Equal("npm", Assert.Single(Assert.Single(plan).Verification).Executable);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"scripts\":{\"test\":\"jest\"}}")]
    [InlineData("{\"scripts\":{\"test\":\"echo no-tests\"}}")]
    public void UnknownChecksRequireExplicitVerification(string manifest)
    {
        Write("package.json", manifest);
        Write("source.zlang", "custom");
        Commit();
        planner.InspectAndRegister("project", repo);
        Assert.Throws<InvalidOperationException>(() => planner.Generate(
            "project", "bad", "Update", ["source.zlang"], "sim", 50));
        var result = planner.Generate("project", "good", "Update",
            ["source.zlang"], "sim", 50, [new("custom-test", ["source.zlang"])]);
        Assert.Equal("custom-test", result.Single().Verification.Single().Executable);
    }

    [Fact]
    public void OversizedAndVendorManifestsCannotContributeChecks()
    {
        Write("package.json", new string(' ', 300000));
        Write("node_modules/demo/package.json", """{"scripts":{"test":"jest"}}""");
        Commit();
        var p = new ProjectDiscovery().Inspect(repo);
        Assert.Empty(p.Verification);
        Assert.Contains(p.Warnings, w => w.Contains("package.json"));
        Assert.DoesNotContain(p.Evidence, e => e.Path.StartsWith("node_modules"));
    }

    [Fact]
    public void ExplicitDagUsesCoreValidationBeforePersistence()
    {
        Write("src/file.txt", "old");
        Commit();
        planner.InspectAndRegister("project", repo);
        TaskContract a = new("a", "Implement", [], ["src"], [], [], ["pass"],
            [new("custom", [])], ["code"], Risk.Low, 1, 1, 1, "sim", 1);
        var b = a with { Id = "b", Dependencies = ["a"] };
        Assert.Throws<ArgumentException>(() =>
            planner.Import("project", "cycle", 10, [a with { Dependencies = ["b"] }, b]));
        Assert.Throws<ArgumentException>(() =>
            planner.Import("project", "missing", 10, [b]));
        Assert.Throws<ArgumentException>(() =>
            planner.Import("project", "duplicate", 10, [a, a]));
        Assert.Throws<ArgumentException>(() =>
            planner.Import("project", "escape", 10, [a with { Owned = ["../x"] }]));
        planner.Import("project", "ok", 10, [a, b]);
        var plan = Assert.Single(state.Plans());
        Assert.Equal(1, plan.ProfileVersion);
        Assert.Equal(Git("rev-parse", "HEAD"), plan.BaselineCommit);
    }

    [Fact]
    public void LinkBlobsAndUntrackedManifestsAreNeverRead()
    {
        Write("link-text", "../../sensitive");
        Commit();
        var oid = Git("rev-parse", "HEAD:link-text");
        Git("update-index", "--add", "--cacheinfo", "120000," + oid + ",AGENTS.md");
        Git("commit", "-qm", "tracked link");
        Write("package.json", """{"scripts":{"test":"jest"}}""");
        var p = new ProjectDiscovery().Inspect(repo);
        Assert.DoesNotContain(p.Evidence, e => e.Path == "AGENTS.md");
        Assert.Empty(p.Verification);
    }

    [Fact]
    public void XmlExternalEntitiesAreRejectedWithoutReadingTarget()
    {
        Write("danger.csproj", """
            <!DOCTYPE Project [<!ENTITY stolen SYSTEM "file:///missing/secret">]>
            <Project><Name>&stolen;</Name></Project>
            """);
        Commit();
        var p = new ProjectDiscovery().Inspect(repo);
        Assert.Contains("Malformed manifest: danger.csproj", p.Warnings);
        Assert.Empty(p.Verification);
    }

    [Fact]
    public void ArchivedInstructionsAreProtectedAndChangedHeadsRequireInspection()
    {
        Write("src/AGENTS.md", "Preserve.");
        Write("src/program.cs", "class Program {}");
        Commit();
        planner.InspectAndRegister("project", repo);
        var check = new[] { new VerificationCommand("custom", []) };
        var tasks = planner.Generate("project", "a", "Update", ["src"],
            "sim", 100, check);
        Assert.Contains("src/AGENTS.md", Assert.Single(tasks).Excluded);
        Assert.Throws<ArgumentException>(() => planner.Generate(
            "project", "bad", "Change", ["src/AGENTS.md"], "sim", 10, check));
        Write("doc.txt", "new commit");
        Commit();
        Assert.Throws<InvalidOperationException>(() => planner.Generate(
            "project", "stale", "Update", ["src"], "sim", 10, check));
    }
    [Fact]
    public void ManySourcesHaveConservativeRootScopeWithInstructionExclusions()
    {
        for (var i = 0; i < 40; i++) Write($"src/File{i}.ts", "export {};");
        Write("src/build/AGENTS.md", "Repository instructions.");
        Write(".GitHub/Instructions/team.md", "Team instructions.");
        Write("package.json", """{"scripts":{"test":"jest"}}""");
        Commit();
        var first = planner.InspectAndRegister("project", repo);
        var plan = Assert.Single(planner.Generate("project", "p", "Update",
            "sim", 10));
        Assert.Equal(["package.json", "src"], plan.Owned.Order().ToArray());
        Assert.Contains("src/build/AGENTS.md", plan.Excluded);
        var imported = planner.Import("project", "q", 10,
            [plan with { Owned = [".GitHub"], Excluded = [] }]);
        Assert.Contains(".GitHub/Instructions/team.md", imported.Single().Excluded);
        var second = planner.InspectAndRegister("project", repo);
        Assert.Equal(2, second.Version);
        Assert.All(state.Plans(), p => Assert.Equal(first.Version, p.ProfileVersion));
        Assert.Throws<InvalidOperationException>(() =>
            state.CreatePlan("project", repo, "stale", [plan], 10, first.Version));
        Assert.DoesNotContain(state.Plans(), p => p.Id == "stale");
    }

    [Fact]
    public void JsonPlanRequiresCompleteStrictArgumentVectorContracts()
    {
        Write("file.ts", "export {};");
        Commit();
        planner.InspectAndRegister("project", repo);
        const string json = """
            [{"id":"one","objective":"Update","dependencies":[],"owned":["file.ts"],
            "readOnly":[],"excluded":[],"criteria":["passes"],"verification":[
            {"executable":"custom","arguments":["test"]}],"deliverables":["file"],
            "risk":"low","maxAttempts":1,"timeoutSeconds":120,"priority":1,
            "runtime":"sim","tokenReservation":10}]
            """;
        var tasks = planner.ImportJson("project", "good", 10, json);
        Assert.Equal("custom", tasks.Single().Verification.Single().Executable);
        Assert.Throws<System.Text.Json.JsonException>(() =>
            planner.ImportJson("project", "bad", 10, json.Replace("priority", "extra")));
    }
    public void Dispose()
    {
        foreach (var file in scratch.EnumerateFiles("*", SearchOption.AllDirectories))
            file.IsReadOnly = false;
        scratch.Delete(true);
    }
}
