using Microsoft.Data.Sqlite;
using Pda.Core;

namespace Pda.Persistence.Tests;

public sealed class ProfileMigrationTests
{
    private sealed record Profile(string Commit, string Note) : IProjectProfile;

    [Fact]
    public void VersionTwoPlansRemainReadableAndProfilesSurviveBackup()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, "state.db");
            var old = new StateStore(path);
            TaskContract task = new("t", "Build", [], ["file"], [], [],
                ["tests"], [new("check", [])], ["output"], Risk.Low, 1, 10,
                0, "sim", 1);
            old.CreatePlan("project", folder.FullName, "old", [task], 1);
            using (var raw = new SqliteConnection(
                $"Data Source={path};Pooling=False"))
            {
                raw.Open();
                using var version = raw.CreateCommand();
                version.CommandText = """
                    DROP TABLE plan_profiles;
                    DROP TABLE project_profiles;
                    PRAGMA user_version=2;
                    """;
                version.ExecuteNonQuery();
            }
            var migrated = new StateStore(path);
            Assert.Null(Assert.Single(migrated.Plans()).ProfileVersion);
            var profile = new Profile(new string('a', 40), "observed");
            migrated.SaveProjectProfile("project", profile, 0);
            migrated.CreatePlan("project", folder.FullName, "new", [task], 1, 1);
            var backup = Path.Combine(folder.FullName, "backup.db");
            migrated.Backup(backup);
            var restored = new StateStore(backup);
            Assert.Equal(profile,
                restored.LatestProjectProfile<Profile>("project")!.Data);
            var newer = restored.Plans().Single(p => p.Id == "new");
            Assert.Equal(profile.Commit, newer.BaselineCommit);
            Assert.Equal(1, newer.ProfileVersion);
        }
        finally { folder.Delete(true); }
    }
}
