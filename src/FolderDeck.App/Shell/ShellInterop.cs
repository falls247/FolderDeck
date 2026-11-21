using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FolderDeck.Shell;

/// <summary>Thin wrappers over the CsWin32-generated shell, GDI and monitor APIs.</summary>
internal static class ShellInterop
{
    private static readonly Guid IidShellItemImageFactory = typeof(IShellItemImageFactory).GUID;

    /// <summary>
    /// <c>IShellItemImageFactory::GetImage</c>. Returns null when the shell cannot produce an image
    /// (no thumbnail handler, path too long, missing file…). Must run on an STA thread.
    /// </summary>
    public static BitmapSource? GetShellImage(string path, int sizePx, SIIGBF flags)
    {
        object? factoryObject = null;
        try
        {
            HRESULT hr = PInvoke.SHCreateItemFromParsingName(path, null, out IShellItemImageFactory factory);
            if (hr.Failed || factory is null)
            {
                return null;
            }

            factoryObject = factory;
            factory.GetImage(new SIZE(sizePx, sizePx), flags, out DeleteObjectHBITMAPSafeHandle bitmap);
            using (bitmap)
            {
                if (bitmap.IsInvalid)
                {
                    return null;
                }

                return FromHBitmap(new HBITMAP(bitmap.DangerousGetHandle()));
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or InvalidCastException or FileNotFoundException or IOException)
        {
            return null;
        }
        finally
        {
            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                Marshal.ReleaseComObject(factoryObject);
            }
        }
    }

    /// <summary>Converts a 32-bpp DIB section keeping its (premultiplied) alpha channel.</summary>
    public static unsafe BitmapSource? FromHBitmap(HBITMAP hbm)
    {
        DIBSECTION ds = default;
        int got = PInvoke.GetObject(new HGDIOBJ(hbm.Value), sizeof(DIBSECTION), &ds);
        if (got >= sizeof(BITMAP) && ds.dsBm.bmBits != null && ds.dsBm.bmBitsPixel == 32 && ds.dsBm.bmWidth > 0 && ds.dsBm.bmHeight != 0)
        {
            int width = ds.dsBm.bmWidth;
            int height = Math.Abs(ds.dsBm.bmHeight);
            int stride = ds.dsBm.bmWidthBytes;
            bool bottomUp = got == sizeof(DIBSECTION) ? ds.dsBmih.biHeight > 0 : true;
            var pixels = new byte[stride * height];
            byte* src = (byte*)ds.dsBm.bmBits;
            fixed (byte* dst = pixels)
            {
                for (int y = 0; y < height; y++)
                {
                    int srcRow = bottomUp ? height - 1 - y : y;
                    Buffer.MemoryCopy(src + ((long)srcRow * stride), dst + ((long)y * stride), stride, stride);
                }
            }

            // All-zero alpha = a bitmap without alpha; all-255 = opaque. Shell bitmaps are usually
            // premultiplied, but some (folder thumbnails) carry straight alpha: a color channel above
            // its alpha gives that away, and those must be shown as Bgra32 or they turn white.
            bool anyAlpha = false;
            bool anyTransparent = false;
            bool straight = false;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                anyAlpha |= a != 0;
                anyTransparent |= a != 255;
                if (!straight && (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a))
                {
                    straight = true;
                }
            }

            var format = !(anyAlpha && anyTransparent)
                ? PixelFormats.Bgr32
                : straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
            var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        try
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap((nint)hbm.Value, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when all four corner pixels are opaque (an image drawn on a square background).</summary>
    public static bool HasOpaqueCorners(BitmapSource image)
    {
        if (image.Format != PixelFormats.Pbgra32 && image.Format != PixelFormats.Bgra32)
        {
            return true; // no alpha channel at all
        }

        int w = image.PixelWidth;
        int h = image.PixelHeight;
        if (w < 2 || h < 2)
        {
            return true;
        }

        var pixel = new byte[4];
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
        {
            image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (pixel[3] < 250)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Small (16 px) or large (32 px) icon via <c>SHGetFileInfo</c>.</summary>
    public static BitmapSource? GetFileInfoIcon(string pathOrName, bool isFolder, bool useAttributesOnly, bool small)
    {
        var info = default(SHFILEINFOW);
        var flags = SHGFI_FLAGS.SHGFI_ICON | (small ? SHGFI_FLAGS.SHGFI_SMALLICON : SHGFI_FLAGS.SHGFI_LARGEICON);
        var attributes = isFolder ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;
        if (useAttributesOnly)
        {
            flags |= SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES;
        }

        nuint ok = PInvoke.SHGetFileInfo(pathOrName, useAttributesOnly ? attributes : 0, ref info, flags);
        if (ok == 0 || info.hIcon.IsNull)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            PInvoke.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>48 px (extra large) or 256 px (jumbo) system image list icon, by name only.</summary>
    public static unsafe BitmapSource? GetSystemImageListIcon(string name, bool isFolder, bool jumbo)
    {
        var info = default(SHFILEINFOW);
        var attributes = isFolder ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;
        nuint ok = PInvoke.SHGetFileInfo(name, attributes, ref info, SHGFI_FLAGS.SHGFI_SYSICONINDEX | SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES);
        if (ok == 0)
        {
            return null;
        }

        object? listObject = null;
        try
        {
            const int SHIL_EXTRALARGE = 2;
            const int SHIL_JUMBO = 4;
            HRESULT hr = PInvoke.SHGetImageList(jumbo ? SHIL_JUMBO : SHIL_EXTRALARGE, out Windows.Win32.UI.Controls.IImageList list);
            if (hr.Failed || list is null)
            {
                return null;
            }

            listObject = list;
            HICON icon;
            const uint ILD_TRANSPARENT = 0x1;
            list.GetIcon(info.iIcon, ILD_TRANSPARENT, &icon);
            if (icon.IsNull)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                PInvoke.DestroyIcon(icon);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (listObject is not null && Marshal.IsComObject(listObject))
            {
                Marshal.ReleaseComObject(listObject);
            }
        }
    }

    /// <summary>8.3 short form of a long path, or null when unavailable.</summary>
    public static string? GetShortPath(string path)
    {
        try
        {
            string input = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : (path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path);
            Span<char> buffer = stackalloc char[1024];
            uint length = PInvoke.GetShortPathName(input, buffer);
            if (length == 0 || length > buffer.Length)
            {
                return null;
            }

            string result = new(buffer[..(int)length]);
            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                result = @"\\" + result[8..];
            }
            else if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                result = result[4..];
            }

            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }

    // ---- monitors ---------------------------------------------------------------------------

    public readonly record struct MonitorArea(Rect Work, double Scale);

    /// <summary>Work area (physical pixels, taskbar excluded) and DPI scale of the monitor at a point.</summary>
    public static unsafe MonitorArea GetMonitorArea(Point physicalPoint)
    {
        var monitor = PInvoke.MonitorFromPoint(
            new System.Drawing.Point((int)Math.Round(physicalPoint.X), (int)Math.Round(physicalPoint.Y)),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        Rect work;
        if (PInvoke.GetMonitorInfo(monitor, ref info))
        {
            var r = info.rcWork;
            work = new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }
        else
        {
            work = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        double scale = 1.0;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0) && PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        return new MonitorArea(work, scale);
    }

    /// <summary>True when the rectangle (physical pixels) touches some monitor.</summary>
    public static bool IsOnAnyMonitor(Rect physical)
    {
        var r = new RECT((int)physical.Left, (int)physical.Top, (int)physical.Right, (int)physical.Bottom);
        return !PInvoke.MonitorFromRect(r, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull;
    }

    public static unsafe Rect? GetWindowRect(IntPtr hwnd)
    {
        if (PInvoke.GetWindowRect(new HWND(hwnd), out RECT r))
        {
            return new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }

        return null;
    }

    public static void MoveWindow(IntPtr hwnd, int x, int y)
    {
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            HWND.Null,
            x,
            y,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    public static unsafe void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ / 11)
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), (Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE)20, &value, sizeof(int));
    }

    public static void AllowAnyForeground() => PInvoke.AllowSetForegroundWindow(unchecked((uint)-1));

    public static void BringToFront(IntPtr hwnd)
    {
        var h = new HWND(hwnd);
        if (PInvoke.IsIconic(h))
        {
            PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        PInvoke.SetForegroundWindow(h);
    }

    public static unsafe void AddToRecentDocs(string path)
    {
        fixed (char* p = path)
        {
            PInvoke.SHAddToRecentDocs((uint)SHARD.SHARD_PATHW, p);
        }
    }
}
