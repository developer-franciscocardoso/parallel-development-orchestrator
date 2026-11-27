using Pda.Security;

namespace Pda.Security.Tests;

public sealed class PolicyTests
{
    [Fact]
    public void StrictOptionsUseNativeProfileAndDenyEscalation()
    {
        var options = CodexPolicy.Options;
        Assert.Contains("default_permissions=\"pda\"", options);
        Assert.Contains("windows.sandbox=\"mxc\"", options);
        Assert.Contains("permissions.pda.network.enabled=false", options);
        Assert.Contains("web_search=\"disabled\"", options);
        Assert.Contains("features.plugins=false", options);
        Assert.Contains("features.apps=false", options);
        Assert.Contains("features.multi_agent=false", options);
        Assert.DoesNotContain("--sandbox", options);
        Assert.DoesNotContain(options, arg => arg.Contains("mcp_servers={}"));
    }
}
