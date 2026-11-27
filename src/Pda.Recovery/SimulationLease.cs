using Pda.Core;

namespace Pda.Recovery;

public sealed record SimulationLease(string Path)
{
    public static string FilePath(string evidenceRoot, string attemptId)
    {
        ContractValidator.ValidateId(attemptId);
        return System.IO.Path.Combine(evidenceRoot, attemptId, "simulation.lock");
    }

    internal WorkerObservation Inspect(string root, string attemptId)
    {
        var file = FilePath(System.IO.Path.GetFullPath(root), attemptId);
        if (!string.Equals(file, Path, StringComparison.Ordinal))
            return Unknown();
        try
        {
            for (var dir = new FileInfo(file).Directory; dir != null;
                 dir = dir.Parent)
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return Unknown();
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                return Unknown();
            using var lease = File.Open(file, FileMode.Open,
                FileAccess.Read, FileShare.None);
            return new(TreeCondition.Stopped, true, "Simulation lease released.");
        }
        catch (IOException error) when (
            OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
        {
            return new(TreeCondition.Live, true, "Simulation lease held.");
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException)
        {
            return Unknown();
        }
    }

    private static WorkerObservation Unknown() => new(TreeCondition.Unknown,
        false, "Simulation lease evidence could not be verified.");
}
