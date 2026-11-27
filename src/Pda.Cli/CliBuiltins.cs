using Pda.Hosting;

namespace Pda.Cli;

internal delegate Task<object> CliFeature(OrchestratorHost host,
    IReadOnlyDictionary<string, string> options, CancellationToken token);

internal static partial class CliBuiltins
{
    static partial void RegisterBenchmark(ref CliFeature? feature);
    static partial void RegisterUpdate(ref CliFeature? feature);

    internal static Task<object> Run(OrchestratorHost host, Arguments input,
        CancellationToken token)
    {
        CliFeature? feature = null;
        if (input.Name == "benchmark") RegisterBenchmark(ref feature);
        else RegisterUpdate(ref feature);
        if (feature == null)
            throw new NotSupportedException(
                input.Name + " is unavailable in this build.");
        var spec = Arguments.Commands.Single(c => c.Name == input.Name);
        var options = spec.Options.Where(input.Has)
            .ToDictionary(k => k, k => input.Required(k));
        return feature(host, options, token);
    }
}
