using Pda.Admission;
using Pda.Agents;
using Pda.Core;
using Pda.Providers;
using Pda.Scheduling;

namespace Pda.Application;

public static class ExecutionServices
{
    public static ExecutionCoordinator Create(IStateStore store,
        ProviderRegistry providers, IEnumerable<IAgentRuntime> runtimes,
        Func<TaskContract, string?> modelFor, string worktreeRoot,
        string evidenceRoot, int concurrency = 6,
        Func<HostPressure>? measure = null)
    {
        var runtimeMap = runtimes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var admission = new AdmissionService(store, providers, measure);
        var workspaces = new PlanWorkspaces(store, worktreeRoot);

        DispatchAssignment? Reserve(string plan)
        {
            var slot = admission.ReserveNext(plan, modelFor, concurrency);
            return slot == null ? null : new DispatchAssignment(slot.Contract,
                slot.Attempt, slot.Model.Model.Id);
        }

        return new ExecutionCoordinator(store, id => runtimeMap[id], Reserve,
            workspaces.Open, worktreeRoot, evidenceRoot);
    }
}
