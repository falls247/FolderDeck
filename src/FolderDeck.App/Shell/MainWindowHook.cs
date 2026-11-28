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
/// away (<c>WM_DEVICECHANGE</c>, for the PC section), the monitors or the work area changed
/// (<c>WM_DISPLAYCHANGE</c>, <c>WM_SETTINGCHANGE</c> with <c>SPI_SETWORKAREA</c>) and the window was
/// activated (<c>WM_ACTIVATE</c>, for the check after a display change). Construct it once the window has its handle
/// (<c>SourceInitialized</c>) and dispose it when the window closes, both on the UI thread. The
/// events are raised on the UI thread, never from inside the window procedure.
/// </summary>
public sealed class MainWindowHook : IDisposable
{
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    /// <summary>A WM_CLOSE this soon after SC_CLOSE comes from the user (see <see cref="IsUserClose"/>).</summary>
    private const long UserCloseWindowMs = 2000;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DEVICECHANGE = 0x0219;
    private const long SPI_SETWORKAREA = 0x002F;
    private const int WA_INACTIVE = 0;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const long DBT_DEVICEARRIVAL = 0x8000;
    private const long DBT_DEVICEREMOVECOMPLETE = 0x8004;

    /// <summary>One device sends several messages (a card reader: one per slot); the list is read once, after the last.</summary>
    private static readonly TimeSpan DrivesDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>A monitor plugged in or a projection mode switched sends a burst (resolution, work area, each monitor): reported once.</summary>
    private static readonly TimeSpan DisplayDebounce = TimeSpan.FromMilliseconds(700);

    private readonly Window _window;
    private readonly HwndSourceHook _hook;
    private readonly DispatcherTimer _drivesTimer;
    private readonly DispatcherTimer _displayTimer;
    private HwndSource? _source;
    private nint _handle;
    private bool _listening;
    private bool _clipboardPending;
    private bool _disposed;
    private long? _sysCloseAt;

    public MainWindowHook(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _hook = WndProc;
        _drivesTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = DrivesDebounce };
        _drivesTimer.Tick += OnDrivesTimer;
        _displayTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = DisplayDebounce };
        _displayTimer.Tick += OnDisplayTimer;

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

    /// <summary>
    /// A monitor was added or removed, a resolution, a projection mode (Win + P) or a work area
    /// (the taskbar) changed: 700 ms after the last such message.
    /// </summary>
    public event Action? DisplayChanged;

    /// <summary>The window was activated (raised after the window procedure, at Input priority).</summary>
    public event Action? Activated;

    /// <summary>
    /// The close under way was asked by the user (the close button, Alt + F4, the taskbar: a
    /// <c>SC_CLOSE</c> just before), not by another program's WM_CLOSE (a build script, taskkill
    /// without /F). 常駐モード hides the window only for the user's.
    /// </summary>
    public bool IsUserClose => _sysCloseAt is { } at && Environment.TickCount64 - at < UserCloseWindowMs;

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
        _displayTimer.Stop();
        _displayTimer.Tick -= OnDisplayTimer;

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
            case WM_SYSCOMMAND when ((int)wParam & 0xFFF0) == SC_CLOSE:
                _sysCloseAt = Environment.TickCount64;
                break;

            case WM_CLIPBOARDUPDATE:
                QueueClipboardChanged();
                break;

            // Every top-level window gets these two for volumes without registering for them. The
            // device type is not checked: reading the drive list once too often costs nothing.
            case WM_DEVICECHANGE when (long)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE:
                _drivesTimer.Stop();
                _drivesTimer.Start();
                break;

            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE when (long)wParam == SPI_SETWORKAREA:
                _displayTimer.Stop();
                _displayTimer.Start();
                break;

            // Posted: a handler that moves the window in the z-order must not run inside the
            // activation (it would come before WPF's own Activated handlers).
            case WM_ACTIVATE when ((long)wParam & 0xFFFF) != WA_INACTIVE:
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RaiseActivated());
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

    private void RaiseActivated()
    {
        if (!_disposed)
        {
            Activated?.Invoke();
        }
    }

    private void OnDisplayTimer(object? sender, EventArgs e)
    {
        _displayTimer.Stop();
        if (!_disposed)
        {
            DisplayChanged?.Invoke();
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
