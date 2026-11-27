using System.Security.Cryptography;
using System.Text;

namespace Pda.Dashboard;

internal sealed record DashboardSession(string Cookie, string Csrf,
    DateTimeOffset Expires);

internal sealed class DashboardSessions
{
    private readonly byte[] key;
    private readonly Dictionary<string, DashboardSession> logins = [];

    public DashboardSessions(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            Encoding.UTF8.GetByteCount(secret) is < 32 or > 512)
            throw new ArgumentException(
                "Set PDA_DASHBOARD_TOKEN to a secret of 32 to 512 UTF-8 bytes.");
        key = Digest(secret);
    }

    public bool Accepts(string input) => input.Length <= 512 &&
        CryptographicOperations.FixedTimeEquals(key, Digest(input));

    public DashboardSession Open()
    {
        lock (logins)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in logins.Values.Where(s => s.Expires <= now)
                .ToArray())
                logins.Remove(item.Cookie);
            if (logins.Count >= 256)
                throw new InvalidOperationException("Session limit reached.");
            var session = new DashboardSession(Random(), Random(),
                now.AddHours(8));
            logins.Add(session.Cookie, session);
            return session;
        }
    }

    public DashboardSession? Find(string? cookie)
    {
        if (cookie is null || cookie.Length != 64) return null;
        lock (logins)
            return logins.TryGetValue(cookie, out var item) &&
                item.Expires > DateTimeOffset.UtcNow ? item : null;
    }

    public void Close(string cookie)
    {
        lock (logins) logins.Remove(cookie);
    }

    private static byte[] Digest(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Random() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
