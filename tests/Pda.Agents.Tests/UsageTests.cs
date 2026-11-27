using Pda.Agents;

namespace Pda.Agents.Tests;

public sealed class UsageTests
{
    private const string Start = """{"type":"turn.started"}""";
    private const string End =
        """{"type":"turn.completed","usage":{"input_tokens":7,"cached_input_tokens":2,"output_tokens":3}}""";

    [Fact]
    public void AccumulatesAcrossCompleteTurns()
    {
        var parser = new CodexEventParser();
        foreach (var line in new[] {Start, End, Start, End})
            Assert.NotNull(parser.Accept(line));
        Assert.Equal(new TokenUsage(14, 4, 6), parser.Usage);
        Assert.Equal(20, parser.Usage!.Total);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"type":"turn.failed"}""")]
    [InlineData("""{"type":"turn.started"}""")]
    [InlineData("""{"type":"turn.completed","usage":null}""")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":-1,"output_tokens":1}}""")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":2,"output_tokens":1}}""")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":9223372036854775807,"output_tokens":1}}""")]
    public void BadEvidencePoisonsWholeStream(string bad)
    {
        var parser = new CodexEventParser();
        parser.Accept(Start);
        parser.Accept(End);
        parser.Accept(Start);
        parser.Accept(bad);
        Assert.Null(parser.Usage);
    }

    [Fact]
    public void ContradictoryTotalPoisonsUsage()
    {
        var parser = new CodexEventParser();
        parser.Accept(Start);
        parser.Accept(End.Replace("\"output_tokens\":3",
            "\"output_tokens\":3,\"total_tokens\":99"));
        Assert.Null(parser.Usage);
    }

    [Fact]
    public void FailedTurnCannotBeRepairedByLaterSuccess()
    {
        var parser = new CodexEventParser();
        parser.Accept(Start);
        parser.Accept("""{"type":"turn.failed"}""");
        parser.Accept(Start);
        parser.Accept(End);
        Assert.Null(parser.Usage);
    }
}
