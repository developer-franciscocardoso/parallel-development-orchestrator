using Pda.Dashboard;

namespace Pda.Dashboard.Tests;

public sealed class SessionTests
{
    [Fact]
    public void SessionsAreIndependentAndLogoutRevokesOneCookie()
    {
        var sessions = new DashboardSessions(new string('x', 32));
        Assert.False(sessions.Accepts("invalid"));
        Assert.True(sessions.Accepts(new string('x', 32)));
        var first = sessions.Open();
        var second = sessions.Open();
        Assert.NotEqual(first.Cookie, second.Cookie);
        Assert.NotEqual(first.Csrf, second.Csrf);
        Assert.Equal(first, sessions.Find(first.Cookie));
        sessions.Close(first.Cookie);
        Assert.Null(sessions.Find(first.Cookie));
        Assert.Equal(second, sessions.Find(second.Cookie));
    }
}
