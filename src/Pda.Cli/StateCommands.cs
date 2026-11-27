using System.Text.Json;
using Pda.Hosting;
using Pda.Persistence;
using Pda.Security;

namespace Pda.Cli;

public static partial class CliApplication
{
    private static object Backup(OrchestratorHost host, string target)
    {
        var destination = NewDatabase(target);
        host.Store.Backup(destination);
        return new { destination };
    }

    private static object Restore(string source, string target)
    {
        var backup = TrustedPaths.File(Path.GetFullPath(source));
        var destination = NewDatabase(target);
        StateStore.Restore(backup, destination);
        return new { source = backup, destination };
    }

    private static string NewDatabase(string target)
    {
        var path = Path.GetFullPath(target);
        TrustedPaths.Directory(Path.GetDirectoryName(path)!);
        if (Path.Exists(path))
            throw new IOException("Destination already exists: " + path);
        return path;
    }

    private static object ImportLegacy(OrchestratorHost host, string path)
    {
        var folder = TrustedPaths.Directory(Path.GetFullPath(path));
        foreach (var name in new[] {"execution-state.json", "task-registry.json"})
            TrustedPaths.File(Path.Combine(folder, name));
        using var guard = host.Store.AcquireStartupGuard();
        return host.Store.ImportLegacy(folder);
    }

    private static object ReconcileLegacy(OrchestratorHost host, Arguments a)
    {
        if (!a.Has("confirm-stopped"))
            throw new UsageError("Supply --confirm-stopped after checking workers.");
        var source = Path.GetFullPath(a.Values[0]);
        var hash = a.Values[1];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new UsageError("An imported 64-digit SHA-256 is required.");
        var totals = JsonSerializer.Deserialize<long[]>(Read(a.Required("usage")))
            ?? throw new UsageError("--usage requires an array of measured totals.");
        if (totals.Any(n => n < 0))
            throw new UsageError("Token totals must be nonnegative.");
        using var guard = host.Store.AcquireStartupGuard();
        host.Store.ReconcileLegacy(source, hash.ToUpperInvariant(), true,
            totals, a.Required("evidence"));
        return new { source, sha256 = hash.ToUpperInvariant(), reconciled = true };
    }
}
