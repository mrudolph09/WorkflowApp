using Microsoft.Win32.SafeHandles;

namespace Workflow.Terminal.Native;

/// <summary>Owns the HPCON returned by CreatePseudoConsole.</summary>
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates the handle wrapper.</summary>
    /// <param name="handle">The raw HPCON.</param>
    internal SafePseudoConsoleHandle(IntPtr handle)
        : base(ownsHandle: true) => SetHandle(handle);

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}
