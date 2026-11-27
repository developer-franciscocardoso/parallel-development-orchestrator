using Pda.Cli;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    stop.Cancel();
};
return await CliApplication.RunAsync(args, Console.Out, Console.Error, stop.Token);
