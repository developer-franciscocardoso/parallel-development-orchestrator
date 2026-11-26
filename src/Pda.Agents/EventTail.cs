using System.Text;
using Pda.Execution;

namespace Pda.Agents;

internal sealed class EventTail : IDisposable
{
    private readonly MemoryStream line = new();
    private readonly byte[] buffer = new byte[8192];
    private readonly UTF8Encoding utf8 = new(false, true);
    private FileStream? stream;
    internal CodexEventParser Parser { get; } = new();

    internal async Task<ProcessOutcome> Follow(string path,
        Task<ProcessOutcome> run, Action<AgentEvent> publish)
    {
        while (!run.IsCompleted)
        {
            Drain(path, publish);
            await Task.WhenAny(run, Task.Delay(40)).ConfigureAwait(false);
        }
        var outcome = await run.ConfigureAwait(false);
        Drain(path, publish);
        if (line.Length != 0 || outcome.OutputTruncated) Parser.Invalidate();
        return outcome;
    }

    private void Drain(string path, Action<AgentEvent> publish)
    {
        if (stream == null && !File.Exists(path)) return;
        stream ??= new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 1);
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            int offset = 0, newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', offset,
                count - offset)) >= 0)
            {
                line.Write(buffer, offset, newline - offset);
                Publish(publish);
                offset = newline + 1;
            }
            line.Write(buffer, offset, count - offset);
        }
    }

    private void Publish(Action<AgentEvent> publish)
    {
        string text;
        try { text = utf8.GetString(line.GetBuffer(), 0, (int)line.Length); }
        catch (DecoderFallbackException) { Parser.Invalidate(); return; }
        finally { line.SetLength(0); }
        if (Parser.Accept(text) is { } evt) publish(evt);
    }

    public void Dispose()
    {
        stream?.Dispose();
        line.Dispose();
    }
}
