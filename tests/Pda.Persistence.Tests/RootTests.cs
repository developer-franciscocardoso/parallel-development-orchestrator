using System.Diagnostics;
using Pda.Core;

namespace Pda.Persistence.Tests;

public sealed class RootTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory();
    private string At(string name) => Path.Combine(root.FullName, name);
    public void Dispose()
    {
        var link = At("alias");
        if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        root.Delete(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedProjectsAreRejectedInBothRegistrationOrders(bool reverse)
    {
        var store = new StateStore(At("test.db"));
        var outer = At("repo");
        var inner = Directory.CreateDirectory(At("repo/src")).FullName;
        store.RegisterProject("first", reverse ? inner : outer);
        var other = reverse ? outer : inner;
        Assert.Throws<InvalidOperationException>(() =>
            store.RegisterProject("second", other));
        TaskContract task = new("t", "build", [], ["module"], [], [], ["ok"],
            [new("test", [])], ["file"], Risk.Low, 1, 1, 1, "sim", 1);
        Assert.Throws<InvalidOperationException>(() =>
            store.CreatePlan("second", other, "plan", [task], 1));
        Assert.Single(store.Projects());
        Assert.Empty(store.Plans());
    }

    [Fact]
    public void RejectsLinkedRootsAndLinkedAncestors()
    {
        var store = new StateStore(At("test.db"));
        var target = Directory.CreateDirectory(At("repo"));
        target.CreateSubdirectory("src");
        var link = At("alias");
        if (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = root.FullName,
                ArgumentList = { "/d", "/c", "mklink", "/j", "alias", "repo" }
            };
            using var junction = Process.Start(info)!;
            junction.WaitForExit();
            Assert.Equal(0, junction.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target.FullName);
        Assert.Throws<InvalidOperationException>(() =>
            store.RegisterProject("link", link));
        Assert.Throws<InvalidOperationException>(() =>
            store.RegisterProject("nested", Path.Combine(link, "src")));
    }
}
