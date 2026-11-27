using System.Runtime.InteropServices;
using System.Windows.Interop;
using FolderDeck.Core.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FolderDeck.Shell;

/// <summary>
/// 常駐モードの通知領域のアイコン (<c>Shell_NotifyIcon</c>, no Windows Forms). Its messages go to a
/// window of its own (an invisible top-level <see cref="HwndSource"/> on the UI thread: a
/// message-only window would miss the <c>TaskbarCreated</c> broadcast that asks to add the icon again
/// after Explorer restarts). Left click: <see cref="ShowRequested"/>; right click: a menu with
/// 表示 / 終了. Created, shown, hidden and disposed on the UI thread; the events are raised there,
/// after the window procedure returned.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint IconId = 1;
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;

    /// <summary>The app icon's resource id in the exe (the SDK's <c>ApplicationIcon</c>).</summary>
    private const int AppIconResource = 32512;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private HICON _icon;
    private bool _added;
    private bool _wanted;
    private bool _disposed;

    public TrayIcon()
    {
        // WS_POPUP without WS_VISIBLE, zero size, no taskbar button (WS_EX_TOOLWINDOW).
        _source = new HwndSource(new HwndSourceParameters("FolderDeck tray")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x00000080,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        });
        _source.AddHook(WndProc);
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Left click on the icon, or 表示 in its menu.</summary>
    public event Action? ShowRequested;

    /// <summary>終了 in the icon's menu.</summary>
    public event Action? ExitRequested;

    /// <summary>Adds or removes the icon (常駐していないときは出さない).</summary>
    public void SetVisible(bool visible)
    {
        if (_disposed)
        {
            return;
        }

        _wanted = visible;
        if (visible && !_added)
        {
            Add();
        }
        else if (!visible && _added)
        {
            Remove();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            Remove();
        }

        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private NOTIFYICONDATAW Data()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = new HWND(_source.Handle),
            uID = IconId,
        };
        return data;
    }

    private void Add()
    {
        if (_icon.IsNull)
        {
            _icon = LoadAppIcon();
        }

        var data = Data();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = "FolderDeck";
        _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
        if (!_added)
        {
            // Explorer not running yet (a start at sign-in can come first): TaskbarCreated adds it later.
            AppLog.Info("resident: the notification area icon could not be added yet; waiting for the taskbar");
        }
    }

    private void Remove()
    {
        var data = Data();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        _added = false;
    }

    private static unsafe HICON LoadAppIcon()
    {
        int cx = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        int cy = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON);
        HINSTANCE instance = new((nint)PInvoke.GetModuleHandle((PCWSTR)null).Value);
        HICON icon;
        HRESULT hr = PInvoke.LoadIconWithScaleDown(instance, (PCWSTR)(char*)AppIconResource, cx, cy, &icon);
        if (hr.Failed || icon.IsNull)
        {
            AppLog.Warn($"resident: the app icon could not be loaded (0x{hr.Value:X8}); the default icon is used");
            hr = PInvoke.LoadIconWithScaleDown(default, (PCWSTR)(char*)AppIconResource, cx, cy, &icon); // IDI_APPLICATION
            return hr.Failed ? default : icon;
        }

        return icon;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch ((int)(lParam & 0xFFFF))
            {
                case WM_LBUTTONUP:
                    _source.Dispatcher.BeginInvoke(() => ShowRequested?.Invoke());
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    _source.Dispatcher.BeginInvoke(ShowMenu);
                    break;
            }

            return 0;
        }

        if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            // Explorer restarted (or started after us): its notification area is empty again.
            _added = false;
            if (_wanted && !_disposed)
            {
                AppLog.Info("resident: the taskbar was created again; adding the icon again");
                Add();
            }
        }

        return 0;
    }

    private unsafe void ShowMenu()
    {
        if (_disposed)
        {
            return;
        }

        HMENU menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull)
        {
            return;
        }

        uint picked;
        try
        {
            Append(menu, ShowCommand, "表示");
            Append(menu, ExitCommand, "終了");
            PInvoke.SetMenuDefaultItem(menu, ShowCommand, 0);
            if (!ShellInterop.TryGetCursorPos(out int x, out int y))
            {
                return;
            }

            var owner = new HWND(_source.Handle);
            PInvoke.SetForegroundWindow(owner); // else the menu stays open when clicking elsewhere
            picked = (uint)PInvoke.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner, (TPMPARAMS*)null).Value;
            PInvoke.PostMessage(owner, WM_NULL, new WPARAM(0), new LPARAM(0));
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }

        switch (picked)
        {
            case ShowCommand:
                ShowRequested?.Invoke();
                break;
            case ExitCommand:
                ExitRequested?.Invoke();
                break;
        }
    }

    private static unsafe void Append(HMENU menu, uint id, string text)
    {
        fixed (char* chars = text)
        {
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, id, chars);
        }
    }
}
