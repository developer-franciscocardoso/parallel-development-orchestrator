using Pda.Packaging;

namespace Pda.Cli;

public static partial class CliApplication
{
    private static object Update(Arguments input, CancellationToken token)
    {
        var root = Path.GetFullPath(input.Required("install-dir"));
        bool status = input.Has("status"), rollback = input.Has("rollback");
        if (status && rollback || (status || rollback) &&
            (input.Has("package") || input.Has("sha256")))
            throw new UsageError("Select exactly one install, rollback or status operation.");
        token.ThrowIfCancellationRequested();
        if (status)
        {
            var installation = PackageInstaller.Status(root);
            return new { installed = installation != null, installation };
        }
        if (rollback) return PackageInstaller.Rollback(root);
        var path = Path.GetFullPath(input.Required("package"));
        var digest = input.Required("sha256");
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new UsageError("--sha256 requires a trusted 64-digit SHA-256.");
        return PackageInstaller.Install(path, digest, root);
    }
}
