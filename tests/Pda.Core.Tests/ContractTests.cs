using Pda.Core;

namespace Pda.Core.Tests;

public class ContractTests
{
    public static TaskContract Task(string id) => new(id, "Build", [],
        [id], [], [], ["tested"], [new("dotnet", ["test"])],
        ["code"], Risk.High, 2, 3600, 1, "simulator", 10);

    [Fact]
    public void RequiresAcyclicDisjointTasks()
    {
        var a = Task("a");
        var b = Task("b") with { Dependencies = ["a"] };
        ContractValidator.Validate([a, b]);
        Assert.Throws<ArgumentException>(() =>
            ContractValidator.Validate([a with { Dependencies = ["b"] }, b]));
        Assert.Throws<ArgumentException>(() =>
            ContractValidator.Validate([a, Task("b") with { Owned = ["a/x"] }]));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("C:/x")]
    [InlineData(".git/config")]
    [InlineData("a/*")]
    public void RejectsUnsafePaths(string path) =>
        Assert.Throws<ArgumentException>(() =>
            ContractValidator.Validate([Task("x") with { Owned = [path] }]));
}
