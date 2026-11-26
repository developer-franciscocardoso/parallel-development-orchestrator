using Pda.Security;

namespace Pda.Security.Tests;

public sealed class BoundaryTests
{
    [Fact]
    public void DuplicateCatalogKeysDeny()
    {
        var plugins = """{"installed":[{"id":"unsafe"}],"installed":[]}""";
        Assert.False(CodexPolicy.EmptyCatalogs("[]", plugins));
    }

    [Fact]
    public void ContradictoryFeatureLinesDeny()
    {
        var rows = CodexPolicy.Options.Where(o => o.StartsWith("features."))
            .Select(o => o.Split('=')[0][9..] + " stable false");
        Assert.False(CodexPolicy.DisabledFeaturesObserved(
            string.Join('\n', rows) + "\nplugins stable true"));
    }

    [Fact]
    public void RejectsVolumeOverlap()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<InvalidDataException>(() =>
            TrustedPaths.Disjoint(root, Path.GetTempPath()));
    }

    [Fact]
    public void RejectsNormalizedDeviceAndNetworkForms()
    {
        Assert.Throws<InvalidDataException>(() =>
            TrustedPaths.Directory("//?/C:/Windows"));
        Assert.Throws<InvalidDataException>(() =>
            TrustedPaths.Directory("//localhost/C$/Windows"));
    }

    [Fact]
    public void PathEntriesCannotSplitIntoUntrustedRelativeDirectories()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string tree = Directory.CreateDirectory(Path.Combine(root, "w")).FullName;
            string home = Directory.CreateDirectory(Path.Combine(root, "h")).FullName;
            string good = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            string bad = Directory.CreateDirectory(Path.Combine(root,
                "sdk;relative")).FullName;
            string exe = Path.Combine(root, "trusted.exe");
            File.Create(exe).Dispose();
            var valid = CodexPolicy.EnvironmentFor(new(exe, home, [good]), tree);
            Assert.Equal([Environment.SystemDirectory, good],
                valid["PATH"].Split(Path.PathSeparator));
            Assert.Throws<InvalidDataException>(() =>
                CodexPolicy.EnvironmentFor(new(exe, home, [bad]), tree));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsSiblingCheckoutTooling()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string tree = Directory.CreateDirectory(Path.Combine(root, "w")).FullName;
            string home = Directory.CreateDirectory(Path.Combine(root, "h")).FullName;
            string bin = Directory.CreateDirectory(Path.Combine(root, "r")).FullName;
            Directory.CreateDirectory(Path.Combine(bin, ".git"));
            string exe = Path.Combine(bin, "tool.exe");
            File.Create(exe).Dispose();
            Assert.Throws<InvalidDataException>(() =>
                CodexPolicy.EnvironmentFor(new(exe, home), tree));
        }
        finally { Directory.Delete(root, true); }
    }
}
