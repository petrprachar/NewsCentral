namespace NewsViewer.Services;

// Must be constructed and SwitchToNew() called before any window handle is created
// on the thread. SetThreadDesktop fails once the thread owns a window.
internal sealed class VirtualDesktopManager : IDisposable
{
    private readonly IntPtr _originalDesktop;
    private readonly IntPtr _newDesktop;
    private bool _disposed;

    internal VirtualDesktopManager()
    {
        _originalDesktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        _newDesktop = NativeMethods.CreateDesktop(
            "NewsViewer", null, IntPtr.Zero, 0, NativeMethods.DESKTOP_ALL_ACCESS, IntPtr.Zero);
        if (_newDesktop == IntPtr.Zero)
            throw new InvalidOperationException("CreateDesktop failed.");
    }

    internal void SwitchToNew()
    {
        NativeMethods.SwitchDesktop(_newDesktop);
        NativeMethods.SetThreadDesktop(_newDesktop);
    }

    internal void SwitchToOriginal()
    {
        NativeMethods.SwitchDesktop(_originalDesktop);
        NativeMethods.SetThreadDesktop(_originalDesktop);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.CloseDesktop(_newDesktop);
    }
}
