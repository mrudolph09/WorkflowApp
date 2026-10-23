using Microsoft.Win32.SafeHandles;

namespace Workflow.Services.Native;

/// <summary>
/// Owns a job object handle. Closing it kills every process still in the job when the job was
/// created with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE - including when this process crashes, because
/// Windows closes the handle then too.
/// </summary>
internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates the wrapper the marshaller fills in.</summary>
    public SafeJobObjectHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
