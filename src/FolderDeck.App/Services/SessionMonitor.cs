using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace FolderDeck.Services;

/// <summary>
/// Makes "the app quit by itself" diagnosable from <c>app.log</c> (all lines start with
/// <c>lifecycle:</c>; <c>tools\analyze-log.ps1</c> summarizes them):
/// <list type="bullet">
/// <item>why the main window closed: the user, another program (WM_CLOSE without a close command —
/// e.g. a build script's <c>CloseMainWindow</c> or <c>taskkill</c> without /F), Windows signing
/// out or shutting down, or Restart Manager (an installer or update);</item>
/// <item>a session record (<see cref="SessionMarker"/>) that a crash or a forced kill leaves
/// behind; the next start reports it together with Windows' crash records for FolderDeck.exe
/// from the Application event log.</item>
/// </list>
/// </summary>
public sealed class SessionMonitor
{
    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    private static readonly TimeSpan AliveInterval = TimeSpan.FromMinutes(1);

    /// <summary>The working set or the private bytes have to move this much before the memory line is logged again.</summary>
    private const long MemoryLogStepMb = 64;

    private readonly SessionMarker _marker;
    private SessionInfo _info;
    private DispatcherTimer? _alive;
    private long? _sysCloseTick;
    private string? _sessionEnding;
    private long _loggedWorkingSetMb = -MemoryLogStepMb;

    /// <summary>The private bytes at the last memory line: a leak grows them, while Windows may trim the working set.</summary>
    private long _loggedPrivateMb = -MemoryLogStepMb;

    public SessionMonitor(string logDirectory, string version)
    {
        _marker = new SessionMarker(logDirectory);
        var now = DateTimeOffset.Now;
        _info = new SessionInfo { Pid = Environment.ProcessId, Version = version, StartedAt = now, LastAliveAt = now };
    }

    /// <summary>Reports a leftover record of the previous session, then starts this session's (UI thread).</summary>
    public void Start(Dispatcher dispatcher)
    {
        var leftover = _marker.ReadLeftover();
        Touch();
        if (leftover is not null)
        {
            var now = DateTimeOffset.Now;
            _ = Task.Run(() => ReportUncleanExit(leftover, now));
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Info($"lifecycle: process exit (code {Environment.ExitCode})");

        // Background priority: a UI thread that hangs stops refreshing LastAliveAt, which shows
        // up in the next start's report.
        _alive = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = AliveInterval };
        _alive.Tick += (_, _) => Touch();
        _alive.Start();
    }

    /// <summary>
    /// The memory line (<see cref="AppServices.DescribeMemory"/>), set once the services exist: logged
    /// with the minute's record when the working set moved by <see cref="MemoryLogStepMb"/> since.
    /// </summary>
    public Func<string>? DescribeMemory { get; set; }

    /// <summary>
    /// Runs inside <c>WM_ENDSESSION</c> (the session really ends, on the UI thread): saves and marks
    /// the clean exit, which <see cref="MarkCleanExit"/> after it repeats harmlessly.
    /// </summary>
    public Action? SessionEnded { get; set; }

    /// <summary>Logs why <paramref name="window"/> is asked to close.</summary>
    public void Watch(Window window)
    {
        void Attach()
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Attach();
        }
        else
        {
            window.SourceInitialized += (_, _) => Attach();
        }

        window.Closed += (_, _) => AppLog.Info("lifecycle: main window closed");
    }

    /// <summary>Last step of a normal exit: the session record goes away.</summary>
    public void MarkCleanExit()
    {
        _alive?.Stop();
        _marker.Delete();
    }

    private void Touch()
    {
        long workingSet = 0;
        long privateBytes = 0;
        try
        {
            using var self = Process.GetCurrentProcess();
            workingSet = self.WorkingSet64;
            privateBytes = self.PrivateMemorySize64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        _info = _info with
        {
            LastAliveAt = DateTimeOffset.Now,
            WorkingSetMb = workingSet / (1024 * 1024),
            ManagedHeapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
        };
        _marker.Write(_info);
        long privateMb = privateBytes / (1024 * 1024);
        if (DescribeMemory is { } describe
            && (Math.Abs(_info.WorkingSetMb - _loggedWorkingSetMb) >= MemoryLogStepMb || Math.Abs(privateMb - _loggedPrivateMb) >= MemoryLogStepMb))
        {
            _loggedWorkingSetMb = _info.WorkingSetMb;
            _loggedPrivateMb = privateMb;
            AppLog.Info(describe());
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SYSCOMMAND when ((int)wParam & 0xFFF0) == SC_CLOSE:
                _sysCloseTick = Environment.TickCount64;
                break;

            case WM_CLOSE:
                string how = _sessionEnding is not null
                    ? $"because Windows is ending the session ({_sessionEnding})"
                    : _sysCloseTick is { } sysClose && Environment.TickCount64 - sysClose < 2000
                        ? "by the user (close button, Alt+F4 or the taskbar)"
                        : "by another program (WM_CLOSE without a close command: e.g. a build script's CloseMainWindow, taskkill without /F, an installer)";
                AppLog.Info($"lifecycle: close requested {how}; foreground app: {ForegroundProcessName()}");
                break;

            case WM_QUERYENDSESSION:
                _sessionEnding = EndSessionText(lParam);
                AppLog.Info($"lifecycle: Windows asks to end the session ({_sessionEnding})");
                break;

            case WM_ENDSESSION:
                AppLog.Info(wParam != 0
                    ? $"lifecycle: Windows is ending the session ({EndSessionText(lParam)})"
                    : "lifecycle: Windows cancelled ending the session");
                if (wParam == 0)
                {
                    _sessionEnding = null;
                }
                else
                {
                    // Windows may end the process once this returns: no shutdown of WPF comes first.
                    SessionEnded?.Invoke();
                }

                break;
        }

        return 0;
    }

    private static string EndSessionText(nint lParam)
    {
        long flags = lParam;
        var parts = new List<string>();
        if ((flags & 0x1) != 0)
        {
            parts.Add("close-app request from Restart Manager: an installer or update needs the file");
        }

        if ((flags & 0x40000000) != 0)
        {
            parts.Add("critical");
        }

        if ((flags & 0x80000000) != 0)
        {
            parts.Add("sign-out");
        }

        return parts.Count == 0 ? "shutdown or restart" : string.Join(", ", parts);
    }

    private static unsafe string ForegroundProcessName()
    {
        try
        {
            HWND hwnd = PInvoke.GetForegroundWindow();
            if (hwnd.IsNull)
            {
                return "(none)";
            }

            uint pid = 0;
            _ = PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            if (pid == 0)
            {
                return "(unknown)";
            }

            if (pid == Environment.ProcessId)
            {
                return "FolderDeck itself";
            }

            using var process = Process.GetProcessById((int)pid);
            return $"{process.ProcessName}.exe (pid {pid})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "(unknown)";
        }
    }

    // ---- previous session --------------------------------------------------------------------

    private static void ReportUncleanExit(SessionInfo previous, DateTimeOffset now)
    {
        if (previous.Pid == 0)
        {
            AppLog.Warn("lifecycle: previous session did not exit cleanly (its session record is unreadable)");
        }
        else
        {
            AppLog.Warn(
                $"lifecycle: previous session did not exit cleanly: pid {previous.Pid}, version {previous.Version}, " +
                $"started {Stamp(previous.StartedAt)}, last alive {Stamp(previous.LastAliveAt)}, " +
                $"working set {previous.WorkingSetMb} MB, managed heap {previous.ManagedHeapMb} MB. " +
                "It crashed, was killed (taskkill /F, Task Manager, Stop-Process) or the PC lost power.");
        }

        var from = previous.Pid == 0 ? now.AddDays(-1) : previous.StartedAt.AddMinutes(-1);
        var records = CrashRecords(from, previous.Pid);
        if (records is null)
        {
            return; // reading the event log failed; already logged
        }

        if (records.Count == 0)
        {
            AppLog.Warn("lifecycle: the Application event log has no crash record for FolderDeck.exe since that start: " +
                        "most likely it was killed by another program or the PC lost power, not a crash.");
            return;
        }

        foreach (var record in records)
        {
            AppLog.Warn("lifecycle: event log crash record: " + record);
        }
    }

    /// <summary>Application Error / .NET Runtime / WER / Application Hang records that mention FolderDeck.</summary>
    private static List<string>? CrashRecords(DateTimeOffset from, int pid)
    {
        const int MaxRecords = 10;
        const int MaxChars = 3000;
        string since = from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string xpath =
            "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] or " +
            "Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang']) and " +
            $"TimeCreated[@SystemTime>='{since}']]]";
        var found = new List<string>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
            for (var record = reader.ReadEvent(); record is not null && found.Count < MaxRecords; record = reader.ReadEvent())
            {
                using (record)
                {
                    string properties = string.Join(" | ", record.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture)));
                    string? message = null;
                    try
                    {
                        message = record.FormatDescription();
                    }
                    catch (EventLogException)
                    {
                    }

                    string text = string.IsNullOrWhiteSpace(message) ? properties : message;
                    if (!text.Contains("FolderDeck", StringComparison.OrdinalIgnoreCase)
                        && !properties.Contains("FolderDeck", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sb = new StringBuilder();
                    sb.Append(record.TimeCreated is { } t ? Stamp(new DateTimeOffset(t)) : "?");
                    sb.Append(' ').Append(record.ProviderName).Append(" (event ").Append(record.Id).Append(")");
                    if (pid != 0 && properties.Contains($"0x{pid:x}", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(" [same pid]");
                    }

                    sb.Append(": ").Append(text.ReplaceLineEndings(" ").Trim());
                    found.Add(sb.Length > MaxChars ? sb.ToString(0, MaxChars) + "…" : sb.ToString());
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("lifecycle: could not read the Application event log", ex);
            return null;
        }

        return found;
    }

    private static string Stamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
