using Pda.Core;

namespace Pda.Persistence.Tests;

public class StoreTests
{
    [Fact]
    public async Task DuplicateReservationsAreSerializedAcrossConnections()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "state.db");
            var db = new StateStore(path);
            TaskContract task = new("a", "Build", [], ["a"], [], [],
                ["passes"], [new("dotnet", ["test"])], ["code"],
                Risk.High, 2, 3600, 1, "simulator", 10);
            db.CreatePlan("project", directory.FullName, "plan", [task], 10);
            var calls = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
                new StateStore(path).Reserve("plan", "a", $"key{i}")));
            var result = await Task.WhenAll(calls);
            Assert.Single(result, r => r is not null);
            Assert.Single(new StateStore(path).Attempts("plan"));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
