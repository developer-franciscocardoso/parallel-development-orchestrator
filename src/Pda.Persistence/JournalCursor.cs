using Pda.Core;

namespace Pda.Persistence;

public sealed partial class StateStore
{
    public IReadOnlyList<JournalEntry> RecentActivity(string plan)
    {
        using var db = database.Open();
        return db.Read("""
            SELECT seq,plan,task,attempt,kind,payload,timestamp FROM journal
            WHERE plan=$p0 ORDER BY seq DESC LIMIT 100
            """, MapJournal, null, plan).AsEnumerable().Reverse().ToArray();
    }

    public IReadOnlyList<JournalEntry> MetricEvents(string plan)
    {
        using var db = database.Open();
        return db.Read("""
            SELECT seq,plan,task,attempt,kind,'',timestamp FROM journal
            WHERE plan=$p0 AND (attempt IS NOT NULL OR kind='PlanCreated'
                OR seq=(SELECT max(seq) FROM journal WHERE plan=$p0))
            ORDER BY seq
            """, MapJournal, null, plan);
    }

    private static JournalEntry MapJournal(Microsoft.Data.Sqlite.SqliteDataReader r)
        => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
            r.GetString(5), DateTimeOffset.Parse(r.GetString(6)));

    public long LatestSequence()
    {
        using var db = database.Open();
        return db.Number("SELECT coalesce(max(seq),0) FROM journal", null);
    }

    public IReadOnlyList<JournalEntry> ReadJournal(long after, int limit,
        string? plan = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.Open();
        const string sql = """
            SELECT seq,plan,task,attempt,kind,payload,timestamp
            FROM journal WHERE seq>$p0
            """;
        var query = sql + (plan == null ? "" : " AND plan=$p2") +
            " ORDER BY seq LIMIT $p1";
        return db.Read(query, r => new JournalEntry(r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
            r.GetString(5), DateTimeOffset.Parse(r.GetString(6))), null,
            plan == null ? [after, limit] : [after, limit, plan]);
    }
}
