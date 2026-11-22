using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace FolderDeck.Shell;

/// <summary>
/// Window messages of the main window that the app reacts to: the clipboard changed
/// (<c>WM_CLIPBOARDUPDATE</c>, for the 貼り付け state and the cut marks) and a volume arrived or went
/// away (<c>WM_DEVICECHANGE</c>, for the PC section). Construct it once the window has its handle
/// (<c>SourceInitialized</c>) and dispose it when the window closes, both on the UI thread. The
/// events are raised on the UI thread, never from inside the window procedure.
/// </summary>
public sealed class MainWindowHook : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const long DBT_DEVICEARRIVAL = 0x8000;
    private const long DBT_DEVICEREMOVECOMPLETE = 0x8004;

    /// <summary>One device sends several messages (a card reader: one per slot); the list is read once, after the last.</summary>
    private static readonly TimeSpan DrivesDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly HwndSourceHook _hook;
    private readonly DispatcherTimer _drivesTimer;
    private HwndSource? _source;
    private nint _handle;
    private bool _listening;
    private bool _clipboardPending;
    private bool _disposed;

    public MainWindowHook(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _hook = WndProc;
        _drivesTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = DrivesDebounce };
        _drivesTimer.Tick += OnDrivesTimer;

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Attach();
        }
        else
        {
            // Constructed too early: wait for the handle rather than watch nothing.
            window.SourceInitialized += OnSourceInitialized;
        }
    }

    /// <summary>
    /// The clipboard's content changed, by any program including this one. Several changes in a row
    /// (a program that writes its formats one by one) are reported once.
    /// </summary>
    public event Action? ClipboardChanged;

    /// <summary>
    /// A volume was added or removed (USB drive, card, disc, mounted image), 500 ms after the last
    /// such message. Not raised for network drives: Windows sends no message for them.
    /// </summary>
    public event Action? DrivesChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _drivesTimer.Stop();
        _drivesTimer.Tick -= OnDrivesTimer;

        if (_listening)
        {
            // Fails harmlessly when the window is already destroyed (Windows dropped the listener with it).
            PInvoke.RemoveClipboardFormatListener(new HWND(_handle));
            _listening = false;
        }

        if (_source is { IsDisposed: false } source)
        {
            source.RemoveHook(_hook);
        }

        _source = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        Attach();
    }

    private void Attach()
    {
        if (_disposed || _source is not null)
        {
            return;
        }

        nint handle = new WindowInteropHelper(_window).Handle;
        HwndSource? source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
        if (source is null)
        {
            AppLog.Warn("Main window hook: the window has no handle; clipboard and drive changes are not watched.");
            return;
        }

        source.AddHook(_hook);
        _source = source;
        _handle = handle;

        _listening = PInvoke.AddClipboardFormatListener(new HWND(handle));
        if (!_listening)
        {
            AppLog.Warn($"Main window hook: AddClipboardFormatListener failed (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Nothing is marked handled: the window and the other hooks see every message as before.
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                QueueClipboardChanged();
                break;

            // Every top-level window gets these two for volumes without registering for them. The
            // device type is not checked: reading the drive list once too often costs nothing.
            case WM_DEVICECHANGE when (long)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE:
                _drivesTimer.Stop();
                _drivesTimer.Start();
                break;
        }

        return 0;
    }

    private void QueueClipboardChanged()
    {
        if (_clipboardPending)
        {
            return;
        }

        // Posted rather than raised here: a handler that opens a dialog or reads the clipboard must
        // not run inside the window procedure, and the messages already queued collapse into one.
        _clipboardPending = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RaiseClipboardChanged());
    }

    private void RaiseClipboardChanged()
    {
        _clipboardPending = false;
        if (!_disposed)
        {
            ClipboardChanged?.Invoke();
        }
    }

    private void OnDrivesTimer(object? sender, EventArgs e)
    {
        _drivesTimer.Stop();
        if (!_disposed)
        {
            DrivesChanged?.Invoke();
        }
    }
}
