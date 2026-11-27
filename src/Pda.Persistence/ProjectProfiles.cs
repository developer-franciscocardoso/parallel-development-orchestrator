using System.Text.Json;
using Pda.Core;

namespace Pda.Persistence;

public sealed partial class StateStore
{
    public Artifact<T> SaveProjectProfile<T>(string project, T profile,
        long expectedVersion) where T : IProjectProfile
    {
        ArgumentNullException.ThrowIfNull(profile);
        var commit = profile.Commit;
        if (commit is not { Length: 40 or 64 } || !commit.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid project profile commit.");
        var data = JsonSerializer.Serialize(profile, profile.GetType());
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var prior = db.Number("""
            SELECT coalesce(max(version),0) FROM project_profiles
            WHERE project=$p0
            """, tx, project);
        if (prior != expectedVersion)
            throw new InvalidOperationException("Stale project profile.");
        var version = checked(prior + 1);
        db.Execute("INSERT INTO project_profiles VALUES($p0,$p1,$p2,$p3)", tx,
            project, version, data, commit);
        Append(db, tx, null, null, null, "ProjectProfileSaved",
            JsonSerializer.Serialize(new { project, version }));
        tx.Commit();
        return new(project, profile, version);
    }

    public Artifact<T>? LatestProjectProfile<T>(string project)
    {
        using var db = database.Open();
        return db.Read("""
            SELECT version,data FROM project_profiles WHERE project=$p0
            ORDER BY version DESC LIMIT 1
            """, r => new Artifact<T>(project,
                JsonSerializer.Deserialize<T>(r.GetString(1))!, r.GetInt64(0)),
            null, project).SingleOrDefault();
    }

    public IReadOnlyList<Artifact<T>> ProjectProfiles<T>(string project)
    {
        using var db = database.Open();
        return db.Read("""
            SELECT version,data FROM project_profiles WHERE project=$p0
            ORDER BY version
            """, r => new Artifact<T>(project,
                JsonSerializer.Deserialize<T>(r.GetString(1))!, r.GetInt64(0)),
            null, project);
    }
}
