using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pda.Execution;

public sealed class ProcessSupervisor : IDisposable
{
    private static string Prefix
    {
        get
        {
            using var current = Process.GetCurrentProcess();
            return $@"Local\Pda.Worker.{current.SessionId}.";
        }
    }
    private readonly CancellationTokenSource disposal = new();
    private readonly object gate = new();
    private bool disposed;

    public async Task<ProcessOutcome> RunAsync(ProcessRequest request,
        Func<SupervisedIdentity, CancellationToken, Task> onStarted,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows jobs required.");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onStarted);
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var interrupt = Link(timeout.Token, cancellationToken);
        interrupt.Token.ThrowIfCancellationRequested();

        using var outLog = OpenLog(request.StandardOutputPath);
        using var errLog = OpenLog(request.StandardErrorPath);
        string name = Prefix + Guid.NewGuid().ToString("N");
        using var job = CreateJob(name);
        using var input = Pipe(PipeDirection.Out);
        using var output = Pipe(PipeDirection.In);
        using var error = Pipe(PipeDirection.In);
        using var child = Launch.Start(request, job, name, input, output, error);
        var stdout = DrainAsync(output, outLog, request.MaxLogBytes, interrupt);
        var stderr = DrainAsync(error, errLog, request.MaxLogBytes, interrupt);
        Task stdin = Task.CompletedTask;
        bool interrupted = false;
        try
        {
            await onStarted(child.Identity, interrupt.Token)
                .ConfigureAwait(false);
            interrupt.Token.ThrowIfCancellationRequested();
            Native.Check(Native.ResumeThread(child.Thread) != uint.MaxValue);
            stdin = Task.Run(() => Send(input, request.StandardInput));
            await child.WaitAsync(interrupt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
        {
            interrupted = true;
        }
        finally
        {
            await StopTree(job).ConfigureAwait(false);
            await Task.WhenAll(stdin, stdout, stderr).ConfigureAwait(false);
        }
        Native.Check(Native.GetExitCodeProcess(child.Process, out int code));
        bool expired = interrupted && timeout.IsCancellationRequested;
        return new(child.Identity, code, expired, interrupted && !expired,
            true, stdout.Result, stderr.Result);
    }

    private static void Validate(ProcessRequest request)
    {
        foreach (var path in new[] { request.Executable,
            request.WorkingDirectory, request.StandardOutputPath,
            request.StandardErrorPath })
        {
            if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
                throw new ArgumentException("Absolute paths required.");
        }
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentNullException.ThrowIfNull(request.StandardInput);
        if (request.MaxLogBytes < 0 || request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request));
    }

    private CancellationTokenSource Link(CancellationToken timeout,
        CancellationToken caller)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(
                caller, timeout, disposal.Token);
        }
    }

    private static SafeFileHandle CreateJob(string name)
    {
        var job = Native.CreateJobObjectW(0, name);
        int error = Marshal.GetLastPInvokeError();
        try
        {
            if (job.IsInvalid || error == 183)
                throw new Win32Exception(error);
            var limits = new Native.Limits();
            limits.Basic.Flags = Native.KillOnJobClose;
            Native.Check(Native.SetInformationJobObject(job,
                Native.JobExtendedLimitInformation, ref limits,
                Marshal.SizeOf<Native.Limits>()));
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static AnonymousPipeServerStream Pipe(PipeDirection direction)
        => new(direction, HandleInheritability.Inheritable);

    private static FileStream OpenLog(string path)
        => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

    private static void Send(Stream pipe, string input)
    {
        using (pipe)
        {
            try
            {
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                writer.Write(input);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232)
            {
                // Worker closed stdin before consuming all input.
            }
        }
    }

    private static Task<bool> DrainAsync(Stream pipe, Stream file, long limit,
        CancellationTokenSource interrupt)
        => Task.Run(() =>
        {
            try { return Drain(pipe, file, limit); }
            catch
            {
                interrupt.Cancel();
                throw;
            }
        });

    private static bool Drain(Stream pipe, Stream file, long limit)
    {
        byte[] buffer = new byte[16384];
        bool truncated = false;
        int count;
        while ((count = pipe.Read(buffer)) > 0)
        {
            int keep = (int)Math.Min(count, limit);
            file.Write(buffer, 0, keep);
            limit -= keep;
            truncated |= keep < count;
        }
        file.Flush();
        return truncated;
    }

    private static async Task StopTree(SafeFileHandle job)
    {
        Native.Check(Native.TerminateJobObject(job, 1));
        var wait = Stopwatch.StartNew();
        while (Native.Active(job) != 0)
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("Unable to confirm job closure.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public static ProcessTreeState Inspect(SupervisedIdentity identity)
    {
        if (!OperatingSystem.IsWindows() || identity is null
            || identity.ProcessId <= 0 || identity.JobName is null
            || !identity.JobName.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(identity.JobName[Prefix.Length..], "N", out _))
            return ProcessTreeState.Unknown;
        using var job = Native.OpenJobObjectW(4, false, identity.JobName);
        if (!job.IsInvalid)
        {
            try
            {
                return Native.Active(job) > 0 ? ProcessTreeState.Live
                    : ProcessTreeState.Stopped;
            }
            catch (Win32Exception) { return ProcessTreeState.Unknown; }
        }
        if (Marshal.GetLastPInvokeError() != 2) return ProcessTreeState.Unknown;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return process.StartTime.ToUniversalTime() == identity.StartedUtc.UtcDateTime
                ? ProcessTreeState.Unknown : ProcessTreeState.Stopped;
        }
        catch (ArgumentException) { return ProcessTreeState.Stopped; }
        catch (InvalidOperationException) { return ProcessTreeState.Unknown; }
        catch (Win32Exception) { return ProcessTreeState.Unknown; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        disposal.Cancel();
        disposal.Dispose();
    }
}
