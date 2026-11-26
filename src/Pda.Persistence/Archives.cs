using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pda.Persistence;

public sealed record LegacyEvidence(string Source, string Sha256,
    int SchemaVersion, int? UnknownAttempts, int? ActiveAttempts);

public sealed partial class StateStore
{
    public void CheckIntegrity()
    {
        using var db = database.Open();
        ValidateCopy(db);
    }

    public void Backup(string destination)
    {
        using var db = database.Open();
        CopyToNew(db, destination);
    }

    public static void Restore(string backup, string newDestination)
    {
        using var db = External(backup, SqliteOpenMode.ReadOnly);
        ValidateCopy(db);
        CopyToNew(db, newDestination);
    }

    private static SqliteConnection External(string file, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(file), Mode = mode, Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void CopyToNew(SqliteConnection source, string destination)
    {
        var target = Path.GetFullPath(destination);
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var db = External(partial, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(db);
                ValidateCopy(db);
                db.Execute("PRAGMA journal_mode=DELETE");
            }
            File.Move(partial, target, overwrite: false);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static void ValidateCopy(SqliteConnection db)
    {
        using var integrity = db.Command("PRAGMA integrity_check", null);
        if (!Equals(integrity.ExecuteScalar(), "ok") ||
            db.Number("SELECT count(*) FROM pragma_foreign_key_check", null) != 0 ||
            db.Number("PRAGMA application_id", null) != Database.ApplicationId)
            throw new InvalidDataException("Invalid or unsupported database.");
        Database.ValidateSchema(db, null, db.Number("PRAGMA user_version", null));
    }

    public IReadOnlyList<LegacyEvidence> ImportLegacy(string executionDirectory)
    {
        var entries = new[] { "execution-state.json", "task-registry.json" }
            .Select(name => ReadLegacy(executionDirectory, name)).ToList();
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        foreach (var (evidence, bytes) in entries)
            db.Execute("""
                INSERT INTO legacy VALUES($p0,$p1,$p2,$p3,$p4)
                ON CONFLICT(source,hash) DO NOTHING
                """, tx, evidence.Source, evidence.Sha256, bytes,
                DateTimeOffset.UtcNow.ToString("O"), JsonSerializer.Serialize(evidence));
        tx.Commit();
        return entries.Select(e => e.Evidence).ToList();
    }

    private static bool UnresolvedLegacy(SqliteConnection db,
        SqliteTransaction tx) => db.Number("""
            SELECT EXISTS(SELECT 1 FROM legacy l
            WHERE (json_extract(evidence,'$.ActiveAttempts') > 0 OR
                   json_extract(evidence,'$.UnknownAttempts') > 0)
            AND NOT EXISTS(SELECT 1 FROM legacy_reconciliations r
                WHERE r.source=l.source AND r.hash=l.hash))
            """, tx) != 0;

    public void ReconcileLegacy(string source, string hash,
        bool confirmedStopped, IReadOnlyList<long> measuredUnknownTokens,
        string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentNullException.ThrowIfNull(measuredUnknownTokens);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var record = db.Read("""
            SELECT evidence FROM legacy WHERE source=$p0 AND hash=$p1
            """, r => JsonSerializer.Deserialize<LegacyEvidence>(r.GetString(0))!,
            tx, source, hash).Single();
        if (record.UnknownAttempts == null || !confirmedStopped ||
            measuredUnknownTokens.Count != record.UnknownAttempts ||
            measuredUnknownTokens.Any(n => n < 0))
            throw new InvalidOperationException("Incomplete legacy reconciliation.");
        var details = JsonSerializer.Serialize(new
        {
            source, hash, confirmedStopped, measuredUnknownTokens, evidence
        });
        db.Execute("INSERT INTO legacy_reconciliations VALUES($p0,$p1,$p2)",
            tx, source, hash, details);
        Append(db, tx, null, null, null, "LegacyReconciled", details);
        tx.Commit();
    }
    public IReadOnlyList<LegacyEvidence> ImportedEvidence()
    {
        using var db = database.Open();
        return db.Read("SELECT evidence FROM legacy ORDER BY source,hash",
            r => JsonSerializer.Deserialize<LegacyEvidence>(r.GetString(0))!,
            null);
    }

    public byte[] ReadLegacyBytes(string source, string sha256)
    {
        using var db = database.Open();
        return db.Read("SELECT original FROM legacy WHERE source=$p0 AND hash=$p1",
            r => (byte[])r[0], null, source, sha256).Single();
    }

    private static (LegacyEvidence Evidence, byte[] Bytes) ReadLegacy(
        string directory, string name)
    {
        var source = Path.GetFullPath(Path.Combine(directory, name));
        var bytes = File.ReadAllBytes(source);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var schema = root.GetProperty("schema_version").GetInt32();
        if (schema != 2) throw new InvalidDataException("Unsupported legacy schema.");
        int? unknown = null;
        int? active = null;
        if (name == "task-registry.json")
        {
            var attempts = root.GetProperty("tasks").EnumerateArray()
                .SelectMany(t => t.GetProperty("attempts").EnumerateArray())
                .ToList();
            unknown = attempts.Count(a => !KnownUsage(a));
            active = attempts.Count(a => a.GetProperty("status").GetString()
                is "running" or "starting");
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return (new(source, hash, schema, unknown, active), bytes);
    }

    private static bool KnownUsage(JsonElement attempt) =>
        attempt.TryGetProperty("usage", out var usage) &&
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty("total_tokens", out var total) &&
        total.ValueKind == JsonValueKind.Number &&
        total.TryGetInt64(out var value) && value >= 0;
}
