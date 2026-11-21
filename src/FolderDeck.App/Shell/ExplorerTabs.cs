using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using Microsoft.CSharp.RuntimeBinder;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FolderDeck.Shell;

/// <summary>
/// "Show / open in Explorer" in a new tab of the most recently active File Explorer window
/// (Windows 11 22H2+). Relies on Explorer's undocumented "new tab" command, so every step is
/// defensive: when no tab can be created, the request falls back to <see cref="ShellActions"/>
/// (a new explorer.exe window); when a requested tab may still appear late, an error is reported
/// instead. One click never opens both a tab and a window.
/// </summary>
public static class ExplorerTabs
{
    /// <summary>Windows 11 22H2: the first build whose File Explorer has tabs.</summary>
    private const int MinBuild = 22621;

    private const uint WmNull = 0x0000;
    private const uint WmCommand = 0x0111;

    /// <summary>Explorer's internal "new tab" command id (undocumented; also used by ExplorerTabUtility).</summary>
    private const uint NewTabCommand = 0xA21B;

    /// <summary>SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED.</summary>
    private const int SelectFlags = 0x1 | 0x4 | 0x8 | 0x10;

    private const int PollMs = 50;
    private const int MaxNavigations = 3;

    /// <summary>How long one "is Explorer's window thread answering?" probe may wait.</summary>
    private const uint IdleProbeMs = 250;

    private static readonly TimeSpan TabAppearTimeout = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan ViewReadyTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RenavigateAfter = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SelectTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>Upper bound for a worker that is stuck in a COM call; it is abandoned after this.</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMilliseconds(5000);

    /// <summary>One request at a time, so two quick clicks cannot pick up each other's new tab.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private enum Route
    {
        /// <summary>The item is shown in a new tab.</summary>
        Tab,

        /// <summary>A new tab was requested but could not be navigated: report an error, open nothing else.</summary>
        TabFailed,

        /// <summary>No tab was created: open a new window as before.</summary>
        Fallback,
    }

    /// <summary>Parent folder in a new tab with the item selected (like <c>explorer.exe /select</c>).</summary>
    public static Task<OpenResult> ShowAsync(string path) => RunAsync(path, select: true);

    /// <summary>The folder itself in a new tab (like <c>explorer.exe "path"</c>).</summary>
    public static Task<OpenResult> OpenAsync(string path) => RunAsync(path, select: false);

    private static async Task<OpenResult> RunAsync(string path, bool select)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinBuild))
        {
            return Fallback(path, select, $"Windows build {Environment.OSVersion.Version.Build} has no Explorer tabs");
        }

        if (!await Gate.WaitAsync(HardTimeout).ConfigureAwait(false))
        {
            return Fallback(path, select, "a previous Explorer tab request is still running");
        }

        try
        {
            var request = new TabRequest(path, select);
            try
            {
                var thread = new Thread(request.Run)
                {
                    IsBackground = true,
                    Name = "ExplorerTab",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch (Exception ex) when (ex is ThreadStartException or OutOfMemoryException or InvalidOperationException)
            {
                AppLog.Warn("Explorer tab: could not start the worker thread", ex);
                return Fallback(path, select, "the worker thread could not start");
            }

            Outcome outcome;
            try
            {
                outcome = await request.Completion.Task.WaitAsync(HardTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                outcome = request.Abandon();
            }

            switch (outcome.Route)
            {
                case Route.Tab:
                    AppLog.Info($"Explorer tab: {path} is shown in a new Explorer tab ({outcome.Reason}).");
                    return new OpenResult(true, null);
                case Route.TabFailed:
                    AppLog.Warn($"Explorer tab: a new tab was requested but {path} could not be shown in it ({outcome.Reason}).");
                    return new OpenResult(false, "エクスプローラーの新しいタブでフォルダを開けませんでした。");
                default:
                    return Fallback(path, select, outcome.Reason);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static OpenResult Fallback(string path, bool select, string reason)
    {
        AppLog.Info($"Explorer tab: {reason}; opening {path} with explorer.exe instead.");
        return select ? ShellActions.ShowInExplorer(path) : ShellActions.OpenInExplorer(path);
    }

    // ---- windows ------------------------------------------------------------------------------

    /// <summary>The visible File Explorer window highest in the Z order (the most recently active one), or 0.</summary>
    private static nint FindExplorerWindow()
    {
        HWND candidate = HWND.Null;
        for (int guard = 0; guard < 1024; guard++)
        {
            candidate = PInvoke.FindWindowEx(HWND.Null, candidate, "CabinetWClass", null);
            if (candidate.IsNull)
            {
                return 0;
            }

            if (PInvoke.IsWindowVisible(candidate) && !IsCloaked(candidate))
            {
                return (nint)candidate;
            }
        }

        return 0;
    }

    /// <summary>The window's tab hosts: on Windows 11 every tab has its own ShellTabWindowClass child.</summary>
    private static HashSet<nint> TabHostsOf(nint window)
    {
        var hosts = new HashSet<nint>();
        HWND host = HWND.Null;
        for (int guard = 0; guard < 1024; guard++)
        {
            host = PInvoke.FindWindowEx(new HWND(window), host, "ShellTabWindowClass", null);
            if (host.IsNull)
            {
                break;
            }

            hosts.Add((nint)host);
        }

        return hosts;
    }

    /// <summary>
    /// True when the thread of <paramref name="hwnd"/> answers messages. A sent message is handled
    /// before the posted ones, so it is asked twice, a poll apart: the second answer normally comes
    /// only after the thread went back to its queue, i.e. after a command posted earlier was dispatched.
    /// </summary>
    private static bool IsIdle(HWND hwnd)
    {
        for (int probe = 0; probe < 2; probe++)
        {
            if (probe > 0)
            {
                Thread.Sleep(PollMs);
            }

            LRESULT sent = PInvoke.SendMessageTimeout(
                hwnd,
                WmNull,
                new WPARAM(0),
                new LPARAM(0),
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
                IdleProbeMs);
            if (sent.Value == 0)
            {
                AppLog.Info($"Explorer tab: the Explorer window did not answer within {IdleProbeMs} ms (error {Marshal.GetLastPInvokeError()}).");
                return false;
            }
        }

        return true;
    }

    /// <summary>Cloaked windows live on another virtual desktop (or are hidden by the shell).</summary>
    private static unsafe bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        HRESULT hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return hr.Succeeded && cloaked != 0;
    }

    // ---- ShellWindows (late-bound COM) --------------------------------------------------------

    /// <summary>ShellWindows entries (one per tab) that belong to <paramref name="hwnd"/>. The caller releases them.</summary>
    private static List<object> EntriesOf(object windows, nint hwnd)
    {
        var result = new List<object>();
        int count = ((dynamic)windows).Count;
        for (int i = 0; i < count; i++)
        {
            object? entry;
            try
            {
                entry = ((dynamic)windows).Item(i);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            if (HwndOf(entry) == unchecked((uint)hwnd))
            {
                result.Add(entry);
            }
            else
            {
                Release(entry);
            }
        }

        return result;
    }

    /// <summary>The entry's top-level window. Window handles have 32 significant bits, so the (possibly sign-extended) value is truncated.</summary>
    private static uint HwndOf(object entry)
    {
        try
        {
            object? value = ((dynamic)entry).HWND;
            return value is null ? 0 : unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (IsComFailure(ex) || ex is FormatException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary>COM identity (the IUnknown pointer); stable while the caller holds the object.</summary>
    private static nint Identity(object entry)
    {
        nint unknown = Marshal.GetIUnknownForObject(entry);
        Marshal.Release(unknown);
        return unknown;
    }

    /// <summary>Waits for a ShellWindows entry of <paramref name="hwnd"/> that is not in <paramref name="known"/>.</summary>
    private static object? WaitForNewTab(object windows, nint hwnd, HashSet<nint> known)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TabAppearTimeout)
        {
            Thread.Sleep(PollMs);
            if (FindNewTab(windows, hwnd, known) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>One look for a ShellWindows entry of <paramref name="hwnd"/> that is not in <paramref name="known"/>; null when there is none yet.</summary>
    private static object? FindNewTab(object windows, nint hwnd, HashSet<nint> known)
    {
        List<object> entries;
        try
        {
            entries = EntriesOf(windows, hwnd);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null; // Explorer busy: the caller looks again later
        }

        object? found = null;
        foreach (object entry in entries)
        {
            if (found is null && !known.Contains(Identity(entry)))
            {
                found = entry;
            }
            else
            {
                Release(entry);
            }
        }

        return found;
    }

    /// <summary><c>Document.Folder.Self.Path</c> of a tab, or null while it shows no folder view.</summary>
    private static string? CurrentFolderOf(object tab)
    {
        object? doc = null;
        object? folder = null;
        object? self = null;
        try
        {
            doc = ((dynamic)tab).Document;
            if (doc is null)
            {
                return null;
            }

            folder = ((dynamic)doc).Folder;
            if (folder is null)
            {
                return null;
            }

            self = ((dynamic)folder).Self;
            if (self is null)
            {
                return null;
            }

            return (string?)((dynamic)self).Path;
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
        finally
        {
            Release(self);
            Release(folder);
            Release(doc);
        }
    }

    private static bool IsAt(object tab, string folder, string shellPath)
    {
        string? current = CurrentFolderOf(tab);
        return current is not null && (SamePath(current, folder) || SamePath(current, shellPath));
    }

    private static bool TryNavigate(object tab, object target, bool log)
    {
        try
        {
            ((dynamic)tab).Navigate2(target);
            return true;
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            if (log)
            {
                AppLog.Warn($"Explorer tab: Navigate2 ({(target is byte[] ? "PIDL" : "path")}) failed: {ex.GetType().Name}: {ex.Message}");
            }

            return false;
        }
    }

    /// <summary>Selects, focuses and scrolls to <paramref name="name"/> in the tab's folder view; true once it has the focus.</summary>
    private static bool TrySelect(object tab, string name, string? shortName)
    {
        object? doc = null;
        object? folder = null;
        object? item = null;
        object? focused = null;
        try
        {
            doc = ((dynamic)tab).Document;
            if (doc is null)
            {
                return false;
            }

            folder = ((dynamic)doc).Folder;
            if (folder is null)
            {
                return false;
            }

            item = ((dynamic)folder).ParseName(name);
            if (item is null && shortName is not null)
            {
                item = ((dynamic)folder).ParseName(shortName);
            }

            if (item is null)
            {
                return false;
            }

            ((dynamic)doc).SelectItem(item, SelectFlags);
            focused = ((dynamic)doc).FocusedItem;
            if (focused is null)
            {
                return false;
            }

            string focusedName = PathTools.DisplayName((string?)((dynamic)focused).Path ?? string.Empty);
            return PathTools.PathEquals(focusedName, name) || (shortName is not null && PathTools.PathEquals(focusedName, shortName));
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return false;
        }
        finally
        {
            Release(focused);
            Release(item);
            Release(folder);
            Release(doc);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>The folder as an absolute PIDL (a byte array Navigate2 accepts), or null.</summary>
    private static unsafe byte[]? PidlOf(string path)
    {
        ITEMIDLIST* pidl = PInvoke.ILCreateFromPath(path);
        if (pidl is null)
        {
            return null;
        }

        try
        {
            uint size = PInvoke.ILGetSize(pidl);
            return size is > 0 and < 65536 ? new ReadOnlySpan<byte>(pidl, (int)size).ToArray() : null;
        }
        finally
        {
            PInvoke.ILFree(pidl);
        }
    }

    private static bool SamePath(string a, string b) =>
        PathTools.PathEquals(PathTools.TrimTrailingSeparators(a), PathTools.TrimTrailingSeparators(b));

    private static bool IsComFailure(Exception ex) =>
        ex is ExternalException or InvalidComObjectException or InvalidCastException or RuntimeBinderException
            or MissingMemberException or TargetInvocationException or ArgumentException or NotSupportedException
            or UnauthorizedAccessException;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
            {
            }
        }
    }

    private sealed record Outcome(Route Route, string Reason)
    {
        public static Outcome Shown(string reason) => new(Route.Tab, reason);

        public static Outcome Failed(string reason) => new(Route.TabFailed, reason);

        public static Outcome Fallback(string reason) => new(Route.Fallback, reason);
    }

    /// <summary>
    /// One request, run on its own STA thread. <see cref="Abandon"/> and the worker race through
    /// <c>_state</c>, so the new-tab command is never posted after the caller fell back to a window.
    /// </summary>
    private sealed class TabRequest(string path, bool select)
    {
        private const int Running = 0;
        private const int Committed = 1;
        private const int Abandoned = 2;

        private int _state;
        private volatile bool _navigated;

        public TaskCompletionSource<Outcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run()
        {
            Outcome outcome;
            try
            {
                outcome = Execute();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Explorer tab: unexpected failure", ex);
                outcome = Volatile.Read(ref _state) != Committed
                    ? Outcome.Fallback("unexpected error before the new tab was requested: " + ex.Message)
                    : _navigated ? Outcome.Shown("an error occurred after navigating: " + ex.Message) : Outcome.Failed(ex.Message);
            }

            Completion.TrySetResult(outcome);
        }

        /// <summary>The worker missed <see cref="HardTimeout"/>: decide without it.</summary>
        public Outcome Abandon()
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running)
            {
                return Outcome.Fallback("Explorer did not respond in time");
            }

            return _navigated
                ? Outcome.Shown("Explorer stopped responding after the navigation")
                : Outcome.Failed("Explorer stopped responding after the new tab was requested");
        }

        private Outcome Execute()
        {
            // "Show" = the parent folder with the item selected; a root has no parent and is just opened.
            string folder = path;
            string? itemPath = null;
            if (select)
            {
                string parent = PathTools.ParentOf(path);
                if (parent.Length > 0)
                {
                    folder = parent;
                    itemPath = path;
                }
            }

            if (!Directory.Exists(folder))
            {
                return Outcome.Fallback("the folder does not exist or cannot be reached");
            }

            nint window = FindExplorerWindow();
            if (window == 0)
            {
                return Outcome.Fallback("no File Explorer window is open");
            }

            HWND tabHost = PInvoke.FindWindowEx(new HWND(window), HWND.Null, "ShellTabWindowClass", null);
            if (tabHost.IsNull)
            {
                return Outcome.Fallback("the Explorer window has no tab host");
            }

            string shellPath = ShellActions.ShellPath(folder);
            byte[]? pidl = PidlOf(shellPath);
            var held = new List<object>();
            var known = new HashSet<nint>();
            object? shell = null;
            object? windows = null;
            object? tab = null;
            try
            {
                Type? type = Type.GetTypeFromProgID("Shell.Application");
                shell = type is null ? null : Activator.CreateInstance(type);
                windows = shell is null ? null : ((dynamic)shell).Windows();
                if (windows is null)
                {
                    return Outcome.Fallback("Shell.Application.Windows() is not available");
                }

                held.AddRange(EntriesOf(windows, window));
                foreach (object entry in held)
                {
                    known.Add(Identity(entry));
                }

                if (held.Count == 0)
                {
                    return Outcome.Fallback("the Explorer window is not listed in ShellWindows");
                }

                if (Interlocked.CompareExchange(ref _state, Committed, Running) != Running)
                {
                    return Outcome.Fallback("the request was abandoned");
                }

                ShellInterop.BringToFront(window);
                HashSet<nint> hostsBefore = TabHostsOf(window);
                if (!PInvoke.PostMessage(tabHost, WmCommand, new WPARAM(NewTabCommand), new LPARAM(0)))
                {
                    return Outcome.Fallback($"PostMessage failed (error {Marshal.GetLastPInvokeError()})");
                }

                tab = WaitForNewTab(windows, window, known);
                if (tab is null)
                {
                    // On a busy Explorer the last poll can block past the deadline and see a stale list,
                    // so look once more after the window's thread had a chance to dispatch the command.
                    bool idle = IsIdle(tabHost);
                    tab = FindNewTab(windows, window, known);
                    if (tab is null)
                    {
                        return Missed(window, hostsBefore, idle);
                    }

                    AppLog.Info("Explorer tab: the new tab appeared after the wait; navigating it.");
                }

                bool arrived = Navigate(tab, folder, shellPath, pidl);
                if (!_navigated)
                {
                    return Outcome.Failed("Navigate2 kept failing");
                }

                if (itemPath is null)
                {
                    return Outcome.Shown(arrived ? "opened" : "navigated, arrival not confirmed");
                }

                bool selected = Select(tab, itemPath);
                return Outcome.Shown((arrived, selected) switch
                {
                    (_, true) => "selected",
                    (true, false) => "opened, selection not confirmed",
                    _ => "navigated, arrival and selection not confirmed",
                });
            }
            finally
            {
                Release(tab);
                foreach (object entry in held)
                {
                    Release(entry);
                }

                Release(windows);
                Release(shell);
            }
        }

        /// <summary>
        /// No new tab was found. A lost command and a tab that is merely late look alike, so a new window
        /// is opened only when the command evidently ran without creating a tab: the window has no new
        /// tab host and its thread answers messages. Otherwise the tab may still appear, so the request is
        /// reported as failed instead of opening a window next to it.
        /// </summary>
        private static Outcome Missed(nint window, HashSet<nint> hostsBefore, bool idle)
        {
            HashSet<nint> hostsAfter = TabHostsOf(window);
            bool newHost = !hostsAfter.IsSubsetOf(hostsBefore);
            string detail = $"tab hosts {hostsBefore.Count} -> {hostsAfter.Count}{(newHost ? " (a new one)" : string.Empty)}, "
                + $"Explorer {(idle ? "idle" : "busy")}";
            if (idle && !newHost)
            {
                AppLog.Info($"Explorer tab: Explorer dispatched the new-tab command but created no tab ({detail}).");
                return Outcome.Fallback($"no new tab appeared within {TabAppearTimeout.TotalSeconds:0.#} s");
            }

            AppLog.Warn($"Explorer tab: no new tab was found, but it may still appear ({detail}); not opening a window.");
            return Outcome.Failed("new tab requested but not found in ShellWindows");
        }

        /// <summary>
        /// Navigates the new tab and waits until it shows the folder. Navigation is repeated when the
        /// tab's own first navigation (Home) overrode it; the PIDL and the path string alternate in
        /// case one of them is not honoured.
        /// </summary>
        private bool Navigate(object tab, string folder, string shellPath, byte[]? pidl)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < ViewReadyTimeout && CurrentFolderOf(tab) is null)
            {
                Thread.Sleep(PollMs);
            }

            int attempts = 0;
            int navigations = 0;
            var last = TimeSpan.Zero;
            while (watch.Elapsed < NavigateTimeout)
            {
                if (_navigated && IsAt(tab, folder, shellPath))
                {
                    return true;
                }

                bool due = !_navigated || (navigations < MaxNavigations && watch.Elapsed - last >= RenavigateAfter);
                if (due)
                {
                    object target = pidl is not null && attempts % 2 == 0 ? pidl : shellPath;
                    if (TryNavigate(tab, target, log: attempts < 2))
                    {
                        _navigated = true;
                        navigations++;
                        last = watch.Elapsed;
                    }

                    attempts++;
                }

                Thread.Sleep(PollMs);
            }

            return _navigated && IsAt(tab, folder, shellPath);
        }

        private static bool Select(object tab, string itemPath)
        {
            string name = PathTools.DisplayName(itemPath);
            string? shortName = itemPath.Length >= 260 && ShellInterop.GetShortPath(itemPath) is { } shortPath
                ? PathTools.DisplayName(shortPath)
                : null;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (TrySelect(tab, name, shortName))
                {
                    return true;
                }

                if (watch.Elapsed >= SelectTimeout)
                {
                    return false;
                }

                Thread.Sleep(PollMs * 2);
            }
        }
    }
}
