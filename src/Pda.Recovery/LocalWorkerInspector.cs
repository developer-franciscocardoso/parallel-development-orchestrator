using System.Text.Json;
using Pda.Execution;
using Pda.Repositories;

namespace Pda.Recovery;

public sealed class LocalWorkerInspector : IWorkerInspector
{
    public WorkerObservation Inspect(WorkerRecord worker)
    {
        var process = ProcessSupervisor.Inspect(new(worker.ProcessId,
            worker.ProcessStartedUtc, worker.JobName));
        var tree = process switch
        {
            ProcessTreeState.Live => TreeCondition.Live,
            ProcessTreeState.Stopped => TreeCondition.Stopped,
            _ => TreeCondition.Unknown
        };
        try
        {
            var git = new GitRepositoryService();
            var state = git.Inspect(worker.Worktree);
            var prefix = Path.TrimEndingDirectorySeparator(
                state.Repository.Root) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var intact = worker.RepositoryCommonDirectory is not null &&
                !state.Repository.CommonDirectory.StartsWith(prefix,
                    comparison) &&
                state.Repository.CommonDirectory.Equals(
                    worker.RepositoryCommonDirectory, comparison) &&
                state.Branch == worker.Branch &&
                git.IsAncestor(state.Repository, worker.BaseCommit,
                    state.Commit);
            return new(tree, intact, JsonSerializer.Serialize(new
            {
                state.Commit, state.Branch, state.IsClean,
                StdoutPresent = File.Exists(worker.StandardOutputPath),
                StderrPresent = File.Exists(worker.StandardErrorPath)
            }));
        }
        catch (Exception error) when (error is GitRepositoryException
            or IOException or UnauthorizedAccessException)
        {
            return new(tree, false, "Repository inspection failed: " +
                error.GetType().Name);
        }
    }
}
