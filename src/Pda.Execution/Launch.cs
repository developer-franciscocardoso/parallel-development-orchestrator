using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pda.Execution;

internal sealed class Launch : IDisposable
{
    internal SafeFileHandle Process { get; }
    internal SafeFileHandle Thread { get; }
    internal SupervisedIdentity Identity { get; }

    private Launch(Native.Child child, string jobName)
    {
        Process = new(child.Process, true);
        Thread = new(child.Thread, true);
        try
        {
            Native.Check(Native.GetProcessTimes(Process,
                out var creation, out _, out _, out _));
            Identity = new(child.Pid, DateTimeOffset.FromFileTime(creation)
                .ToUniversalTime(), jobName);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static Launch Start(ProcessRequest request, SafeFileHandle job,
        string jobName, AnonymousPipeServerStream input,
        AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        var startup = new Native.Startup
        {
            Size = Marshal.SizeOf<Native.Startup>(),
            Flags = Native.StartfUseStdHandles,
            Input = input.ClientSafePipeHandle.DangerousGetHandle(),
            Output = output.ClientSafePipeHandle.DangerousGetHandle(),
            Error = error.ClientSafePipeHandle.DangerousGetHandle()
        };
        using var attributes = new LaunchAttributes(job.DangerousGetHandle(),
            startup.Input, startup.Output, startup.Error);
        startup.Attributes = attributes.Pointer;
        var args = request.Arguments.Prepend(request.Executable).Select(Quote);
        var command = new StringBuilder(string.Join(" ", args));
        nint environment = EnvironmentBlock(request.Environment);
        try
        {
            const uint flags = Native.CreateSuspended | Native.CreateNoWindow
                | Native.CreateUnicodeEnvironment
                | Native.ExtendedStartupInfoPresent;
            Native.Check(Native.CreateProcessW(request.Executable, command,
                0, 0, true, flags, environment, request.WorkingDirectory,
                ref startup, out var child));
            return new(child, jobName);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
        }
    }

    private static nint EnvironmentBlock(
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null) return 0;
        var sorted = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('=')
                || key.Contains('\0') || value is null || value.Contains('\0'))
                throw new ArgumentException("Invalid environment entry.");
            sorted.Add(key, value);
        }
        var entries = sorted.Select(pair => $"{pair.Key}={pair.Value}");
        return Marshal.StringToHGlobalUni(string.Join('\0', entries) + '\0');
    }

    private static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0'))
            throw new ArgumentException("NUL in process argument.");
        var quoted = new StringBuilder().Append('"');
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            int count = character == '"' ? checked(slashes * 2 + 1) : slashes;
            quoted.Append('\\', count).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', checked(slashes * 2)).Append('"').ToString();
    }

    internal async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            uint result = Native.WaitForSingleObject(Process, 0);
            if (result == 0) return;
            Native.Check(result == 258); // WAIT_TIMEOUT
            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Thread.Dispose();
        Process.Dispose();
    }
}
