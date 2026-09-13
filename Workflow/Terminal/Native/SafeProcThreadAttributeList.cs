using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Workflow.Terminal.Native;

/// <summary>Owns the unmanaged PROC_THREAD_ATTRIBUTE_LIST used to attach the pseudo-console.</summary>
internal sealed class SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeProcThreadAttributeList(IntPtr handle)
        : base(ownsHandle: true) => SetHandle(handle);

    /// <summary>Allocates a one-entry list and attaches the pseudo-console to it.</summary>
    /// <param name="pseudoConsole">The pseudo-console handle to attach.</param>
    /// <returns>The initialised attribute list.</returns>
    internal static SafeProcThreadAttributeList Create(SafePseudoConsoleHandle pseudoConsole)
    {
        var size = IntPtr.Zero;

        // First call always fails; it reports the required size through `size`.
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var buffer = Marshal.AllocHGlobal(size);
        var list = new SafeProcThreadAttributeList(buffer);

        if (!NativeMethods.InitializeProcThreadAttributeList(buffer, 1, 0, ref size))
        {
            list.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList ist fehlgeschlagen.");
        }

        list._initialised = true;

        if (!NativeMethods.UpdateProcThreadAttribute(
                buffer,
                0,
                NativeMethods.ProcThreadAttributePseudoConsole,
                pseudoConsole.DangerousGetHandle(),
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            list.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute ist fehlgeschlagen.");
        }

        return list;
    }

    private bool _initialised;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (_initialised)
        {
            NativeMethods.DeleteProcThreadAttributeList(handle);
        }

        Marshal.FreeHGlobal(handle);
        return true;
    }
}
