using Pda.Core;

namespace Pda.Quality.Tests;

public sealed class ScopeTests
{
    private readonly TaskContract task = new("t", "Implement feature", [],
        ["src"], ["src/shared"], ["src/private"], ["Works"],
        [new("test", [])], ["Change"], Risk.Low, 1, 30, 0, "sim", 50);

    [Theory]
    [InlineData("src/App.cs", true)]
    [InlineData("src2/App.cs", false)]
    [InlineData("src/Shared/App.cs", false)]
    [InlineData("src/private/file", false)]
    [InlineData("src/AGENTS.md", false)]
    [InlineData("src/.codex/config.toml", false)]
    [InlineData("src/.gitmodules", false)]
    [InlineData("src/../outside", false)]
    public void ScopeProtectsLiteralPathsAndNewControlFiles(string path,
        bool expected) => Assert.Equal(expected, QualityScope.Allows(task, path));
}
