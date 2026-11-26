using Microsoft.Data.Sqlite;

namespace Pda.Persistence;

internal sealed class Database
{
    internal const int ApplicationId = 1346650418;
    private readonly string connectionString;

    internal Database(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absolute, ForeignKeys = true, Pooling = false
        }.ToString();
        using var db = Open();
        if (db.Number("""
            SELECT count(*) FROM sqlite_schema WHERE
            (SELECT application_id FROM pragma_application_id) <> $p0
            """, null, ApplicationId) != 0)
            throw new InvalidDataException("Unrecognized database.");
        db.Execute("PRAGMA journal_mode=WAL");
        using var tx = db.BeginTransaction(deferred: false);
        var version = db.Number("PRAGMA user_version", tx);
        if (version > 0 && db.Number("PRAGMA application_id", tx) != ApplicationId)
            throw new InvalidDataException("Unrecognized database.");
        if (version > 1) throw new InvalidDataException("Newer schema.");
        if (version == 0) db.Execute(Schema, tx);
        tx.Commit();
    }

    internal SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private const string Schema = """
        CREATE TABLE projects(id TEXT PRIMARY KEY, root TEXT NOT NULL UNIQUE);
        CREATE TABLE plans(id TEXT PRIMARY KEY, project TEXT NOT NULL
            REFERENCES projects, budget INTEGER NOT NULL CHECK(budget>=0));
        CREATE TABLE tasks(plan TEXT REFERENCES plans, id TEXT NOT NULL,
            contract TEXT NOT NULL, PRIMARY KEY(plan,id));
        CREATE TABLE dependencies(plan TEXT, task TEXT, dependency TEXT,
            PRIMARY KEY(plan,task,dependency),
            FOREIGN KEY(plan,task) REFERENCES tasks(plan,id),
            FOREIGN KEY(plan,dependency) REFERENCES tasks(plan,id));
        CREATE TABLE attempts(id TEXT PRIMARY KEY, plan TEXT NOT NULL,
            task TEXT NOT NULL, number INTEGER NOT NULL, state TEXT NOT NULL,
            version INTEGER NOT NULL, reserved INTEGER NOT NULL,
            used INTEGER CHECK(used>=0), request TEXT NOT NULL UNIQUE,
            requested_limit INTEGER NOT NULL CHECK(requested_limit BETWEEN 1 AND 6),
            UNIQUE(plan,task,number),
            FOREIGN KEY(plan,task) REFERENCES tasks(plan,id));
        CREATE UNIQUE INDEX active_assignment ON attempts(plan,task)
            WHERE state IN ('Reserved','Running','AwaitingVerification','Verified');
        CREATE TABLE journal(seq INTEGER PRIMARY KEY, plan TEXT REFERENCES plans,
            task TEXT, attempt TEXT REFERENCES attempts, kind TEXT NOT NULL,
            payload TEXT NOT NULL, timestamp TEXT NOT NULL);
        CREATE INDEX journal_plan ON journal(plan,seq);
        CREATE TRIGGER journal_no_update BEFORE UPDATE ON journal
            BEGIN SELECT RAISE(ABORT,'immutable journal'); END;
        CREATE TRIGGER journal_no_delete BEFORE DELETE ON journal
            BEGIN SELECT RAISE(ABORT,'immutable journal'); END;
        CREATE TABLE artifacts(plan TEXT REFERENCES plans, kind TEXT NOT NULL,
            id TEXT NOT NULL, data TEXT NOT NULL, version INTEGER NOT NULL,
            PRIMARY KEY(plan,kind,id));
        CREATE TABLE legacy(source TEXT NOT NULL, hash TEXT NOT NULL,
            original BLOB NOT NULL, imported TEXT NOT NULL, evidence TEXT NOT NULL,
            PRIMARY KEY(source,hash));
        PRAGMA application_id=1346650418;
        PRAGMA user_version=1;
        """;
}

internal static class Sql
{
    internal static SqliteCommand Command(this SqliteConnection db,
        string sql, SqliteTransaction? tx, params object?[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    internal static void Execute(this SqliteConnection db, string sql,
        SqliteTransaction? tx = null, params object?[] args)
    {
        using var cmd = db.Command(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    internal static long Number(this SqliteConnection db, string sql,
        SqliteTransaction? tx, params object?[] args)
    {
        using var cmd = db.Command(sql, tx, args);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    internal static List<T> Read<T>(this SqliteConnection db, string sql,
        Func<SqliteDataReader, T> map, SqliteTransaction? tx,
        params object?[] args)
    {
        using var cmd = db.Command(sql, tx, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }
}
