using Pda.Core;

namespace Pda.Core.Tests;

public class BoundsTests
{
    [Fact]
    public void RejectsOversizedChainsBeforeTraversal()
    {
        var chain = Enumerable.Range(0, 20000).Select(i =>
            ContractTests.Task("t" + i) with
            { Dependencies = i == 19999 ? [] : ["t" + (i + 1)] }).ToList();
        Assert.Throws<ArgumentException>(() => ContractValidator.Validate(chain));
    }

    [Fact]
    public void RejectsOversizedSingleContract()
    {
        var t = ContractTests.Task("x") with { Objective = new string('x', 65536) };
        Assert.Throws<ArgumentException>(() => ContractValidator.Validate([t]));
    }
}
