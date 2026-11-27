using Pda.Execution;
using Pda.Security;

namespace Pda.Agents;

public static class CodexSandboxProbe
{
    public static async Task<bool> VerifyAsync(CodexProfile profile,
        string worktree, CancellationToken token)
    {
        var environment = CodexPolicy.EnvironmentFor(profile, worktree);
        Requests.CheckConfig(worktree, profile.Home);
        var options = CodexPolicy.WorkerOptions(profile, worktree).ToArray();
        using var owner = new ProcessSupervisor();
        return await new CodexProbe(profile, worktree, environment, owner)
            .Sandbox(options, token);
    }
}
