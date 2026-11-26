using System.Runtime.InteropServices;

namespace Pda.Execution;

// Restrict inherited handles and assign the job atomically at creation.
// The latter also closes the crash window before an explicit assignment.
internal sealed class LaunchAttributes : IDisposable
{
    private readonly List<nint> allocations = [];
    private bool initialized;
    internal nint Pointer { get; private set; }

    internal LaunchAttributes(nint job, params nint[] redirectedHandles)
    {
        try
        {
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(0, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            Native.Check(Native.InitializeProcThreadAttributeList(
                Pointer, 2, 0, ref size));
            initialized = true;
            // PROC_THREAD_ATTRIBUTE_HANDLE_LIST
            Add(0x20002, redirectedHandles);
            Add(0x2000D, [job]); // PROC_THREAD_ATTRIBUTE_JOB_LIST
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Add(nuint attribute, nint[] handles)
    {
        int bytes = checked(handles.Length * IntPtr.Size);
        nint storage = Marshal.AllocHGlobal(bytes);
        allocations.Add(storage);
        Marshal.Copy(handles, 0, storage, handles.Length);
        Native.Check(Native.UpdateProcThreadAttribute(Pointer, 0, attribute,
            storage, (nuint)bytes, 0, 0));
    }

    public void Dispose()
    {
        if (initialized) Native.DeleteProcThreadAttributeList(Pointer);
        initialized = false;
        foreach (nint pointer in allocations) Marshal.FreeHGlobal(pointer);
        allocations.Clear();
        Marshal.FreeHGlobal(Pointer);
        Pointer = 0;
    }
}
