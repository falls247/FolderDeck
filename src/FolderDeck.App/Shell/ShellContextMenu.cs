using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FolderDeck.Shell;

public enum ShellMenuOutcome
{
    /// <summary>
    /// The user picked a command and it was handed to the shell. <c>Error</c> is set when the command
    /// reported a failure (the handler has usually shown its own message by then).
    /// </summary>
    Invoked,

    /// <summary>The menu was closed without a command (Esc, a click elsewhere), or the command was cancelled.</summary>
    Dismissed,

    /// <summary>
    /// The shell gave no menu for these items (gone, not file-system items, no common handler), the
    /// background menu of a folder names none of its verbs, or another native menu is still open.
    /// Nothing was shown.
    /// </summary>
    Unavailable,

    /// <summary>The menu was not ready in time (a slow or hung shell extension). It will not appear later.</summary>
    TimedOut,

    /// <summary>
    /// An unexpected error on the menu thread. It may come before or after the menu showed (see
    /// <see cref="ShellMenuResult.Shown"/>).
    /// </summary>
    Failed,

    /// <summary>
    /// One of the caller's entries (put above the shell's) was picked: see
    /// <see cref="ShellMenuResult.AppIndex"/>. Nothing was handed to the shell; the caller runs it.
    /// </summary>
    AppCommand,
}

/// <summary>
/// <c>Verb</c> is the canonical verb of the invoked command when the handler names one
/// (<c>open</c>, <c>properties</c>…), else null. <c>AppIndex</c> is, for
/// <see cref="ShellMenuOutcome.AppCommand"/> only, the index of the picked entry in the list the
/// caller passed (else -1). <c>Shown</c> is whether the menu was on screen (or had been) when the
/// result was reported.
/// </summary>
public sealed record ShellMenuResult(ShellMenuOutcome Outcome, string? Verb, string? Error, int AppIndex = -1, bool Shown = false);

/// <summary>
/// An entry of the caller's own, put above the shell's entries. Win32 menus get the text only (no
/// glyph). When an entry is <c>IsDefault</c>, the shell's <c>open</c> is taken out and this entry
/// becomes the default (bold) one (top level only). A submenu is an entry with <c>IsSubmenu</c> (never
/// picked itself) followed by its entries, whose <c>Parent</c> is its index (-1: the top level). An
/// entry with <c>IsChecked</c> gets a check mark (✓, MF_CHECKED; entries without children only).
/// </summary>
public readonly record struct ShellMenuAppEntry(string Text, bool Enabled, bool IsSeparator, bool IsDefault, int Parent = -1, bool IsSubmenu = false, bool IsChecked = false);

/// <summary>
/// Shows Explorer's own context menu (<c>IContextMenu</c>) for files and folders, so that entries of
/// installed shell extensions (7-Zip, 送る, プログラムから開く…) can be used from the app.
/// <para>
/// Every menu runs on its own pumped STA thread with its own invisible owner window. The owner is
/// deliberately not owned by the main window: ownership across threads attaches the input queues,
/// and a hung extension would then freeze the UI as well. A hung extension therefore costs one
/// abandoned background thread and a <see cref="ShellMenuOutcome.TimedOut"/> result; an extension
/// that crashes still takes the process down. The owner is a plain Win32 window, not a WPF
/// <c>HwndSource</c>: an <c>HwndSource</c> on a new thread registers with WPF's stylus input (a
/// pen thread shared with the UI thread, which creates and destroys the cascade's popups at the
/// same moment) and starts WPF's rendering, all inside the build timeout.
/// </para>
/// <para>
/// The entries for delete, rename, cut, copy and paste are removed: the app has its own, and the
/// shell's delete can be permanent. undo, redo and format go too (see <see cref="RemovedVerbs"/>).
/// Only one menu is open at a time.
/// </para>
/// <para>
/// <see cref="ShowBackgroundAsync"/> shows the background menu of a folder instead (what Explorer
/// shows on the blank area of that folder: 新規作成, プロパティ, ターミナルで開く…), built by the
/// folder's own <c>IShellFolder.CreateViewObject</c>. A drive or share root is allowed there.
/// </para>
/// <para>
/// The caller may put its own entries at the top (<see cref="ShellMenuAppEntry"/>, then a
/// separator, then the shell's). They get the ids from <see cref="AppFirstId"/> up, outside the
/// range handed to the handlers, are never asked for a verb and are never invoked here: a picked
/// one is reported as <see cref="ShellMenuOutcome.AppCommand"/>. With such entries the shell's
/// <c>copyaspath</c> is taken out too (the app has its own パスをコピー), and the shell's
/// <c>open</c> as well when one of them is the default. They are plain strings, not owner-drawn.
/// </para>
/// </summary>
public static class ShellContextMenu
{
    // Command ids handed to the handlers. TrackPopupMenuEx returns 0 for "nothing picked", so the
    // range starts at 1.
    private const uint FirstId = 1;
    private const uint LastId = 0x7FFF;

    // Ids of the caller's entries: far above LastId (some handlers go slightly past idCmdLast) and
    // below the SC_* system command ids.
    private const uint AppFirstId = 0xE000;
    private const uint AppIdLimit = 0xF000;

    /// <summary><c>fByPos</c> of SetMenuDefaultItem: the item is named by its command id.</summary>
    private const uint SetDefaultByCommand = 0;

    private const uint CMF_NORMAL = 0x0000;
    private const uint CMF_EXTENDEDVERBS = 0x0100;
    private const uint GCS_VERBW = 0x0004;

    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int WM_NULL = 0x0000;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_MENUCHAR = 0x0120;

    private const int SW_SHOWNORMAL = 1;

    /// <summary>The window class of the invisible owner (see <see cref="Session.CreateOwner"/>).</summary>
    private const string OwnerClassName = "FolderDeckShellMenuOwner";

    private const int ErrorClassAlreadyExists = 1410;

    /// <summary>A build that took longer than this is logged with the time of each phase.</summary>
    private const long SlowBuildMs = 1000;

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c>: the user backed out of the command (an elevation prompt…).</summary>
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private const int EAbort = unchecked((int)0x80004004);

    /// <summary>Characters of the verb buffer; handlers are told <see cref="VerbChars"/>, the buffer is larger.</summary>
    private const int VerbChars = 260;

    private const int VerbBufferChars = 1024;

    private const string TimedOutMessage = "メニューの準備が時間内に終わりませんでした。";

    private const int StageBuilding = 0;
    private const int StageShowing = 1;
    private const int StageCancelled = 2;

    /// <summary>How long the handlers may take to build the menu. There is no limit once it shows.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the thread keeps pumping after a command at least: handlers start work that still
    /// needs this thread's message loop (a dialog, a posted message).
    /// </summary>
    private static readonly TimeSpan MinPumpAfterInvoke = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PumpCheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a command may run before the result is reported without waiting for it (a handler
    /// that shows a modal window returns only when the window is closed).
    /// </summary>
    private static readonly TimeSpan InvokeReportDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Verbs of the entries taken out of the menu, and refused when picked anyway. The app has its
    /// own delete, rename, cut, copy and paste (the shell's delete can be permanent, and copies and
    /// moves go through the app's transfer only). undo / redo are Explorer's session-wide history:
    /// they can undo a copy, a move or a new file anywhere (that is, delete it), unrelated to the
    /// folder shown. format is a safeguard: a drive root's background menu is not expected to offer it.
    /// </summary>
    private static readonly HashSet<string> RemovedVerbs =
        new(StringComparer.OrdinalIgnoreCase) { "delete", "rename", "cut", "copy", "paste", "undo", "redo", "format" };

    /// <summary>
    /// Also taken out when the caller puts its own entries in: they duplicate the app's. Not
    /// refused after a pick like <see cref="RemovedVerbs"/> (they are harmless). "open" is added
    /// when a caller's entry is the default; it must never go into RemovedVerbs.
    /// </summary>
    private static readonly HashSet<string> RemovedWithAppEntries =
        new(StringComparer.OrdinalIgnoreCase) { "copyaspath" };

    /// <summary>1 while a menu is being built or shown (not while a finished one still pumps).</summary>
    private static int _open;

    /// <summary>
    /// Menu threads not ended yet: building, showing, pumping after a command, or abandoned after a
    /// timeout (still inside an extension). For the logs only.
    /// </summary>
    private static int _liveThreads;

    /// <summary>
    /// Shows the menu for <paramref name="paths"/> at a screen point (physical pixels) and completes
    /// when the menu is closed, the picked command has been handed over, or the menu could not be
    /// shown. The task never faults.
    /// <para>
    /// <paramref name="extended"/> adds the entries Explorer shows with Shift (read on the UI thread
    /// when the app's own menu opened). <paramref name="mainWindow"/> gets the foreground back
    /// afterwards, but only if nothing else took it meanwhile. Roots (drives, shares) are refused.
    /// </para>
    /// <para>
    /// <paramref name="appEntries"/> are put at the top of the menu (see the class remarks); the
    /// caller runs a picked one itself (<see cref="ShellMenuOutcome.AppCommand"/>).
    /// </para>
    /// </summary>
    public static Task<ShellMenuResult> ShowAsync(
        IReadOnlyList<string> paths, int screenX, int screenY, bool extended, nint mainWindow,
        IReadOnlyList<ShellMenuAppEntry>? appEntries = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ShellMenuAppEntry[] app = CopyAppEntries(appEntries);

        // A copy: the caller's list must not change under the menu thread.
        var seen = new HashSet<string>(PathTools.Comparer);
        var targets = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                targets.Add(path);
            }
        }

        // A drive's menu would offer フォーマット; the callers do not offer the menu there either.
        if (targets.Count == 0 || !targets.TrueForAll(path => TransferRules.IsTransferable(path)))
        {
            return Task.FromResult(new ShellMenuResult(ShellMenuOutcome.Unavailable, null, "この項目のメニューは表示できません。"));
        }

        return Start(targets, screenX, screenY, extended, mainWindow, app, background: false);
    }

    /// <summary>
    /// Shows the background menu of <paramref name="folder"/> (Explorer's menu of the blank area of
    /// that folder: 新規作成, プロパティ, ターミナルで開く, entries of shell extensions) at a screen
    /// point, as <see cref="ShowAsync"/> does for items. The same verbs are removed; when none of
    /// the entries names its verb the menu is not shown (the shell's paste could not be taken out).
    /// Unlike <see cref="ShowAsync"/>, a drive or share root is allowed: フォーマット and 取り出し
    /// belong to the drive's item menu, not to the background of its root (format is removed anyway).
    /// </summary>
    public static Task<ShellMenuResult> ShowBackgroundAsync(
        string folder, int screenX, int screenY, bool extended, nint mainWindow,
        IReadOnlyList<ShellMenuAppEntry>? appEntries = null)
    {
        ShellMenuAppEntry[] app = CopyAppEntries(appEntries);
        if (string.IsNullOrWhiteSpace(folder) || !PathTools.IsFullyQualified(folder))
        {
            return Task.FromResult(new ShellMenuResult(ShellMenuOutcome.Unavailable, null, "このフォルダのメニューは表示できません。"));
        }

        return Start([folder], screenX, screenY, extended, mainWindow, app, background: true);
    }

    /// <summary>A copy of the caller's entries (the list must not change under the menu thread).</summary>
    private static ShellMenuAppEntry[] CopyAppEntries(IReadOnlyList<ShellMenuAppEntry>? appEntries)
    {
        Debug.Assert(AppFirstId > LastId);
        ShellMenuAppEntry[] app = appEntries?.ToArray() ?? [];
        if (app.Length > AppIdLimit - AppFirstId)
        {
            throw new ArgumentException("Too many menu entries.", nameof(appEntries));
        }

        // A submenu's entries follow it: InsertAppEntries fills each submenu before its entries come.
        for (int i = 0; i < app.Length; i++)
        {
            int parent = app[i].Parent;
            if (parent != -1 && (parent < 0 || parent >= i || !app[parent].IsSubmenu))
            {
                throw new ArgumentException("A menu entry is in no submenu before it.", nameof(appEntries));
            }
        }

        return app;
    }

    private static Task<ShellMenuResult> Start(
        List<string> targets, int screenX, int screenY, bool extended, nint mainWindow, ShellMenuAppEntry[] app, bool background)
    {
        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
        {
            return Task.FromResult(new ShellMenuResult(ShellMenuOutcome.Unavailable, null, "別のメニューを表示中です。"));
        }

        var session = new Session(targets, screenX, screenY, extended, mainWindow, app, background);
        session.Start();
        return session.Completion;
    }

    private static string MessageOf(HRESULT hr) =>
        Marshal.GetExceptionForHR(hr.Value)?.Message ?? "メニューを取得できませんでした。";

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    // ---- menu items ----

    private static unsafe bool TryGetItem(HMENU menu, int position, out MENUITEMINFOW item)
    {
        MENUITEMINFOW info = default;
        info.cbSize = (uint)sizeof(MENUITEMINFOW);
        info.fMask = MENU_ITEM_MASK.MIIM_FTYPE | MENU_ITEM_MASK.MIIM_ID | MENU_ITEM_MASK.MIIM_SUBMENU;
        bool ok = PInvoke.GetMenuItemInfo(menu, (uint)position, true, &info);
        item = info;
        return ok;
    }

    private static bool IsSeparator(in MENUITEMINFOW item) => (item.fType & MENU_ITEM_TYPE.MFT_SEPARATOR) != 0;

    /// <summary>
    /// The canonical verb of a command (its offset from <see cref="FirstId"/>), or null when the
    /// handler has none or fails. Handlers fail here in many ways, so nothing is let through.
    /// </summary>
    private static unsafe string? VerbOf(IContextMenu contextMenu, uint offset)
    {
        // Larger than what the handler is told: some write past cchMax.
        char* buffer = stackalloc char[VerbBufferChars];
        new Span<char>(buffer, VerbBufferChars).Clear();
        try
        {
            contextMenu.GetCommandString((nuint)offset, GCS_VERBW, null, (byte*)buffer, (uint)VerbChars);
        }
        catch (Exception)
        {
            return null;
        }

        buffer[VerbBufferChars - 1] = '\0';
        string verb = new(buffer);
        return verb.Length == 0 ? null : verb;
    }

    /// <summary>
    /// Takes the app's own operations out of the top level of the menu, and the verbs of
    /// <paramref name="extra"/>. An entry whose verb cannot be read stays; the caller's entries
    /// (ids above <see cref="LastId"/>) are not touched. Returns whether the handler named a verb
    /// for any shell entry at all: when it names none, nothing can be recognised and the shell's
    /// delete may still be in the menu.
    /// </summary>
    private static bool RemoveVerbs(IContextMenu contextMenu, HMENU menu, HashSet<string>? extra)
    {
        bool anyVerb = false;
        for (int i = PInvoke.GetMenuItemCount(menu) - 1; i >= 0; i--)
        {
            if (!TryGetItem(menu, i, out MENUITEMINFOW item)
                || IsSeparator(item)
                || !item.hSubMenu.IsNull
                || item.wID < FirstId
                || item.wID > LastId)
            {
                continue;
            }

            if (VerbOf(contextMenu, item.wID - FirstId) is not { } verb)
            {
                continue;
            }

            anyVerb = true;
            if (RemovedVerbs.Contains(verb) || extra?.Contains(verb) == true)
            {
                PInvoke.DeleteMenu(menu, (uint)i, MENU_ITEM_FLAGS.MF_BYPOSITION);
            }
        }

        return anyVerb;
    }

    /// <summary>Removes a separator that is first, last or follows another one (what the removals leave behind).</summary>
    private static void CollapseSeparators(HMENU menu)
    {
        bool separatorOrEndBelow = true;
        for (int i = PInvoke.GetMenuItemCount(menu) - 1; i >= 0; i--)
        {
            if (!TryGetItem(menu, i, out MENUITEMINFOW item) || !IsSeparator(item))
            {
                separatorOrEndBelow = false;
                continue;
            }

            if (separatorOrEndBelow)
            {
                PInvoke.DeleteMenu(menu, (uint)i, MENU_ITEM_FLAGS.MF_BYPOSITION);
            }

            separatorOrEndBelow = true;
        }

        if (PInvoke.GetMenuItemCount(menu) > 0 && TryGetItem(menu, 0, out MENUITEMINFOW first) && IsSeparator(first))
        {
            PInvoke.DeleteMenu(menu, 0, MENU_ITEM_FLAGS.MF_BYPOSITION);
        }
    }

    // ---- after the command ----

    private static unsafe uint ThreadOf(HWND window)
    {
        uint processId = 0;
        return PInvoke.GetWindowThreadProcessId(window, &processId);
    }

    /// <summary>Whether the thread shows a top-level window (a dialog a handler opened). The owner never is visible.</summary>
    private static bool HasVisibleWindow(uint threadId)
    {
        bool visible = false;
        PInvoke.EnumThreadWindows(
            threadId,
            (window, _) =>
            {
                if (PInvoke.IsWindowVisible(window))
                {
                    visible = true;
                    return false;
                }

                return true;
            },
            new LPARAM(0));
        return visible;
    }

    /// <summary>
    /// Pumps messages on the menu thread for <see cref="MinPumpAfterInvoke"/> and for as long as the
    /// thread shows a window. Also ends when a callback failed (<see cref="StaThread.RunPumped"/>
    /// exits all frames).
    /// </summary>
    private static void PumpAfterInvoke(Dispatcher dispatcher, HWND owner)
    {
        uint threadId = ThreadOf(owner);
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PumpCheckInterval };
        timer.Tick += (_, _) =>
        {
            if (watch.Elapsed >= MinPumpAfterInvoke && (threadId == 0 || !HasVisibleWindow(threadId)))
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>The PIDLs and COM objects of one menu; everything is created and released on the menu thread.</summary>
    private sealed class Bound
    {
        /// <summary>Absolute PIDLs of the targets (<c>ILFree</c>); 0 where none was created.</summary>
        public nint[] Pidls { get; set; } = [];

        /// <summary>
        /// The parent <c>IShellFolder</c>, the <c>IShellItemArray</c> for targets with several
        /// parents, or the folder's own <c>IShellFolder</c> for a background menu.
        /// </summary>
        public object? Parent { get; set; }

        public IContextMenu? Menu { get; set; }

        /// <summary>True when the menu comes from an item array (targets with several parents), not from one parent folder.</summary>
        public bool FromItemArray { get; set; }

        public unsafe void Release()
        {
            ShellContextMenu.Release(Menu);
            Menu = null;
            ShellContextMenu.Release(Parent);
            Parent = null;
            foreach (nint pidl in Pidls)
            {
                if (pidl != 0)
                {
                    PInvoke.ILFree((ITEMIDLIST*)pidl);
                }
            }

            Pidls = [];
        }
    }

    /// <summary>One call of <see cref="ShowAsync"/>: the menu thread's work and the result the caller waits for.</summary>
    private sealed class Session
    {
        private readonly List<string> _paths;
        private readonly int _x;
        private readonly int _y;
        private readonly bool _extended;
        private readonly nint _mainWindow;

        /// <summary>The caller's entries, at the top of the menu (may be empty).</summary>
        private readonly ShellMenuAppEntry[] _app;

        /// <summary>The submenus of <see cref="_app"/> (menu thread only): their WM_INITMENUPOPUP is not the handlers'.</summary>
        private readonly HashSet<nuint> _appPopups = new();

        /// <summary>The background menu of the one folder in <see cref="_paths"/>, not the menu of items.</summary>
        private readonly bool _background;
        private readonly TaskCompletionSource<ShellMenuResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Building → Showing (the menu thread, right before the menu) or Building → Cancelled (the
        /// timeout). Whoever wins the exchange decides; a cancelled menu is never shown.
        /// </summary>
        private int _stage = StageBuilding;

        private int _completed;
        private Timer? _timer;

        // Set only while the menu is tracked; read by the window hook on the menu thread.
        private IContextMenu2? _menu2;
        private IContextMenu3? _menu3;

        /// <summary>The session of this menu thread, for <see cref="OwnerProc"/> (each menu thread runs one session).</summary>
        [ThreadStatic]
        private static Session? _current;

        private static readonly object OwnerClassGate = new();
        private static bool _ownerClassRegistered;

        /// <summary>Kept for the life of the process: the window class calls it through a marshalled pointer.</summary>
        private static readonly WNDPROC OwnerProcDelegate = OwnerProc;

        // Where the build is, for the logs: the phase is read by the timeout (another thread), the
        // times of the finished phases only by the menu thread.
        private readonly Stopwatch _clock = new();
        private volatile string _phase = "thread";
        private long _phaseStart;
        private readonly List<string> _phaseTimes = new(5);

        public Session(List<string> paths, int x, int y, bool extended, nint mainWindow, ShellMenuAppEntry[] app, bool background)
        {
            _paths = paths;
            _x = x;
            _y = y;
            _extended = extended;
            _mainWindow = mainWindow;
            _app = app;
            _background = background;
        }

        public Task<ShellMenuResult> Completion => _result.Task;

        public void Start()
        {
            _clock.Start();
            _timer = new Timer(_ => OnBuildTimeout(), null, BuildTimeout, Timeout.InfiniteTimeSpan);

            // Counted before the thread starts, so that one that hangs at once is counted too.
            Interlocked.Increment(ref _liveThreads);

            // The task of the thread is not awaited: the result is reported as soon as it is known,
            // while the thread may go on pumping for a command it started.
            var thread = StaThread.RunPumped(
                "FolderDeck shell menu",
                dispatcher =>
                {
                    Run(dispatcher);

                    // Not expected: every way out of Run has reported by now.
                    Complete(ShellMenuOutcome.Failed, null, null);
                    return true;
                },
                ex =>
                {
                    AppLog.Warn($"Shell menu failed: {Describe()}", ex);
                    Complete(ShellMenuOutcome.Failed, null, ex.Message);
                    return false;
                });
            _ = thread.ContinueWith(_ => Interlocked.Decrement(ref _liveThreads), TaskScheduler.Default);
        }

        private string Describe() =>
            _background ? $"background of {_paths[0]}"
            : _paths.Count == 1 ? _paths[0] : $"{_paths[0]} (+{_paths.Count - 1})";

        /// <summary>The build goes on to <paramref name="phase"/> (menu thread).</summary>
        private void EnterPhase(string phase)
        {
            long now = _clock.ElapsedMilliseconds;
            _phaseTimes.Add($"{_phase} {now - _phaseStart}");
            _phaseStart = now;
            _phase = phase;
        }

        /// <summary>
        /// The build ended (the menu is about to show, or the timeout came first): logged when it was
        /// slow, with the time of each phase (menu thread).
        /// </summary>
        private void EndBuild(bool tooLate)
        {
            long built = _clock.ElapsedMilliseconds;
            _phaseTimes.Add($"{_phase} {built - _phaseStart}");
            if (built >= SlowBuildMs)
            {
                string what = tooLate ? "ready too late" : "slow to build";
                AppLog.Info($"Shell menu {what}, {built} ms ({string.Join(", ", _phaseTimes)} ms): {Describe()}");
            }
        }

        private void OnBuildTimeout()
        {
            if (Interlocked.CompareExchange(ref _stage, StageCancelled, StageBuilding) != StageBuilding)
            {
                return;
            }

            // The thread is left to itself: when the extension returns after all, it finds the stage
            // cancelled, shows nothing and cleans up. The phase tells the app's part (thread, owner)
            // from the shell's and its extensions' (bind, query, verbs).
            AppLog.Warn(
                $"Shell menu timed out in {_phase} after {_clock.ElapsedMilliseconds} ms " +
                $"(menu threads alive: {Volatile.Read(ref _liveThreads)}): {Describe()}");
            Complete(ShellMenuOutcome.TimedOut, null, TimedOutMessage);
        }

        /// <summary>
        /// Creates the invisible owner on the menu thread: a plain Win32 window (see the class
        /// remarks), its class registered once for the process. Throws when it cannot be created.
        /// </summary>
        private unsafe HWND CreateOwner()
        {
            HINSTANCE instance = new((nint)PInvoke.GetModuleHandle((PCWSTR)null).Value);
            fixed (char* className = OwnerClassName)
            fixed (char* title = "FolderDeck shell menu owner")
            {
                lock (OwnerClassGate)
                {
                    if (!_ownerClassRegistered)
                    {
                        var windowClass = new WNDCLASSEXW
                        {
                            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                            lpfnWndProc = OwnerProcDelegate,
                            hInstance = instance,
                            lpszClassName = className,
                        };
                        if (PInvoke.RegisterClassEx(in windowClass) == 0
                            && Marshal.GetLastPInvokeError() is int error and not ErrorClassAlreadyExists)
                        {
                            throw new Win32Exception(error);
                        }

                        _ownerClassRegistered = true;
                    }
                }

                // Invisible and empty, placed at the click so that a handler's dialog opens near it.
                // No parent: see the class remarks.
                HWND owner = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, title, WINDOW_STYLE.WS_POPUP,
                    _x, _y, 0, 0, HWND.Null, HMENU.Null, instance, null);
                if (owner.IsNull)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return owner;
            }
        }

        /// <summary>
        /// The owner's window procedure: hands the menu messages to the handlers (<see cref="Hook"/>),
        /// the rest to <c>DefWindowProc</c>. Nothing may escape it (it would end the process).
        /// </summary>
        private static LRESULT OwnerProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            try
            {
                if (_current is { } session && session.Hook(msg, wParam, lParam, out nint result))
                {
                    return new LRESULT(result);
                }

                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception)
            {
                return new LRESULT(0);
            }
        }

        /// <summary>
        /// Appends the caller's entries (and a separator after them) to the still empty menu.
        /// <paramref name="shellIndex"/> is where the shell's entries go. False when the menu
        /// could not be filled. A submenu is created when its entry comes and attached to it at once
        /// (the DestroyMenu of <paramref name="menu"/> then destroys it too); its entries follow it.
        /// </summary>
        private unsafe bool InsertAppEntries(HMENU menu, out uint shellIndex)
        {
            shellIndex = 0;
            var popups = new HMENU[_app.Length];
            for (int i = 0; i < _app.Length; i++)
            {
                var entry = _app[i];
                HMENU into = entry.Parent < 0 ? menu : popups[entry.Parent];
                var grayed = entry.Enabled ? default : MENU_ITEM_FLAGS.MF_GRAYED;
                string text = entry.Text.Replace("&", "&&", StringComparison.Ordinal); // no mnemonics
                bool ok;
                if (entry.IsSeparator)
                {
                    ok = PInvoke.AppendMenu(into, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
                }
                else if (entry.IsSubmenu)
                {
                    HMENU popup = PInvoke.CreatePopupMenu();
                    ok = !popup.IsNull && AppendString(into, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_POPUP | grayed, (nuint)popup.Value, text);
                    if (ok)
                    {
                        popups[i] = popup;
                        _appPopups.Add((nuint)popup.Value);
                    }
                    else if (!popup.IsNull)
                    {
                        PInvoke.DestroyMenu(popup); // not attached: not destroyed with the menu
                    }
                }
                else
                {
                    var check = entry.IsChecked ? MENU_ITEM_FLAGS.MF_CHECKED : default;
                    ok = AppendString(into, MENU_ITEM_FLAGS.MF_STRING | grayed | check, AppFirstId + (uint)i, text);
                }

                if (!ok)
                {
                    return false;
                }
            }

            if (_app.Length > 0 && !PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null))
            {
                return false;
            }

            int count = PInvoke.GetMenuItemCount(menu);
            if (count < 0)
            {
                return false;
            }

            shellIndex = (uint)count;
            return true;
        }

        /// <summary><paramref name="id"/>: the command id, or the submenu's handle with MF_POPUP.</summary>
        private static unsafe bool AppendString(HMENU menu, MENU_ITEM_FLAGS flags, nuint id, string text)
        {
            fixed (char* chars = text)
            {
                return PInvoke.AppendMenu(menu, flags, id, chars);
            }
        }

        /// <summary>Reports the result once; later calls are ignored. Any thread.</summary>
        private void Complete(ShellMenuOutcome outcome, string? verb, string? error, int appIndex = -1)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _timer, null)?.Dispose();

            // Before the result: a continuation may ask for the next menu at once.
            Volatile.Write(ref _open, 0);
            _result.TrySetResult(new ShellMenuResult(outcome, verb, error, appIndex, Volatile.Read(ref _stage) == StageShowing));
        }

        private unsafe void Run(Dispatcher dispatcher)
        {
            EnterPhase("owner");
            HWND owner = HWND.Null;
            HMENU menu = default;
            var bound = new Bound();
            _current = this;
            try
            {
                owner = CreateOwner();

                EnterPhase("bind");
                string? bindError = Bind(owner, bound);
                if (bound.Menu is not { } contextMenu)
                {
                    Complete(ShellMenuOutcome.Unavailable, null, bindError ?? "メニューを取得できませんでした。");
                    return;
                }

                menu = PInvoke.CreatePopupMenu();
                if (menu.IsNull)
                {
                    Complete(ShellMenuOutcome.Unavailable, null, "メニューを作成できませんでした。");
                    return;
                }

                // The caller's entries first; the shell's go below them.
                if (!InsertAppEntries(menu, out uint shellIndex))
                {
                    Complete(ShellMenuOutcome.Unavailable, null, "メニューを作成できませんでした。");
                    return;
                }

                // The count in the return value is not needed: TPM_RETURNCMD and the id range do.
                EnterPhase("query");
                HRESULT filled = contextMenu.QueryContextMenu(menu, shellIndex, FirstId, LastId, CMF_NORMAL | (_extended ? CMF_EXTENDEDVERBS : 0u));
                if (filled.Failed)
                {
                    Complete(ShellMenuOutcome.Unavailable, null, MessageOf(filled));
                    return;
                }

                HashSet<string>? extra = null;
                if (_app.Length > 0)
                {
                    extra = new HashSet<string>(RemovedWithAppEntries, StringComparer.OrdinalIgnoreCase);
                    if (Array.Exists(_app, e => e.IsDefault))
                    {
                        // By verb, here only: never refused after a pick (see RemovedWithAppEntries).
                        extra.Add("open");
                    }
                }

                EnterPhase("verbs");
                bool anyVerb = RemoveVerbs(contextMenu, menu, extra);
                if (!anyVerb && (bound.FromItemArray || _background))
                {
                    // The item-array menu names no verbs, so the shell's delete (permanent on a
                    // share or a removable drive) could not be taken out: not shown at all. Same
                    // for a background menu, whose paste could not be told apart. The menu of one
                    // parent folder is known to name its verbs and is left as it is.
                    AppLog.Warn($"Shell menu not shown, its commands cannot be told apart: {Describe()}");
                    Complete(ShellMenuOutcome.Unavailable, null, "メニューを取得できませんでした。");
                    return;
                }

                CollapseSeparators(menu);

                // After the handlers and the removals: replaces the default the shell set.
                int defaultEntry = Array.FindIndex(_app, e => e.IsDefault && e.Enabled && !e.IsSeparator && !e.IsSubmenu && e.Parent < 0); // the top menu only
                if (defaultEntry >= 0)
                {
                    PInvoke.SetMenuDefaultItem(menu, AppFirstId + (uint)defaultEntry, SetDefaultByCommand);
                }

                if (PInvoke.GetMenuItemCount(menu) <= 0)
                {
                    Complete(ShellMenuOutcome.Unavailable, null, "表示できる項目がありません。");
                    return;
                }

                // The last moment to give up: once the menu shows, only the user closes it.
                if (Interlocked.CompareExchange(ref _stage, StageShowing, StageBuilding) != StageBuilding)
                {
                    // The timeout reports this itself; said here too in case this thread is faster.
                    EndBuild(tooLate: true);
                    Complete(ShellMenuOutcome.TimedOut, null, TimedOutMessage);
                    return;
                }

                Interlocked.Exchange(ref _timer, null)?.Dispose();
                EndBuild(tooLate: false);

                int picked;
                _menu2 = contextMenu as IContextMenu2;
                _menu3 = contextMenu as IContextMenu3;
                try
                {
                    // Without the foreground the menu would not close on a click elsewhere or on
                    // Esc, and the invisible owner cannot be activated by the user afterwards. The
                    // foreground is refused when the user went to another application while the
                    // menu was built: then nothing is shown.
                    if (!PInvoke.SetForegroundWindow(owner) && (nint)PInvoke.GetForegroundWindow() != (nint)owner)
                    {
                        AppLog.Info($"Shell menu not shown, the app is no longer in the foreground: {Describe()}");
                        Complete(ShellMenuOutcome.Dismissed, null, null);
                        return;
                    }

                    picked = PInvoke.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, _x, _y, owner, (TPMPARAMS*)null).Value;

                    // The documented companion of SetForegroundWindow + TrackPopupMenu.
                    PInvoke.PostMessage(owner, WM_NULL, new WPARAM(0), new LPARAM(0));
                }
                finally
                {
                    _menu2 = null;
                    _menu3 = null;
                }

                // Before the range check and the verb lookup: GetCommandString on an app id would
                // hand the handler an offset it never gave out.
                long appIndex = (long)picked - AppFirstId;
                if (appIndex >= 0 && appIndex < _app.Length && !_app[appIndex].IsSeparator && !_app[appIndex].IsSubmenu)
                {
                    // The foreground first: the caller's command may show a confirmation.
                    GiveForegroundBack(owner);
                    AppLog.Info($"Shell menu: app entry {appIndex} on {_paths.Count} item(s)");
                    Complete(ShellMenuOutcome.AppCommand, null, null, (int)appIndex);
                    return;
                }

                if (picked < FirstId || picked > LastId)
                {
                    GiveForegroundBack(owner);
                    Complete(ShellMenuOutcome.Dismissed, null, null);
                    return;
                }

                uint offset = (uint)picked - FirstId;
                string? verb = VerbOf(contextMenu, offset);
                if (verb is not null && RemovedVerbs.Contains(verb))
                {
                    // Not expected: these entries were taken out of the top level. One that got
                    // through anyway (in a submenu, or its verb was unreadable before) is not run:
                    // the shell's delete can be permanent.
                    AppLog.Warn($"Shell menu command '{verb}' refused: {Describe()}");
                    GiveForegroundBack(owner);
                    Complete(ShellMenuOutcome.Dismissed, verb, null);
                    return;
                }

                string? invokeError = null;
                bool cancelled = false;

                // A handler may show a modal window from inside InvokeCommand (a confirmation, an
                // error) and return only when it is closed. The caller is not kept waiting for
                // that: the command counts as handed over after a moment. Complete reports once.
                var early = new Timer(_ => Complete(ShellMenuOutcome.Invoked, verb, null), null, InvokeReportDelay, Timeout.InfiniteTimeSpan);
                try
                {
                    Invoke(contextMenu, owner, offset);
                }
                catch (Exception ex)
                {
                    // Handlers report failures with any HRESULT, mapped to any exception type.
                    cancelled = ex.HResult is ErrorCancelled or EAbort;
                    if (!cancelled)
                    {
                        invokeError = ex.Message;
                        AppLog.Warn($"Shell menu command '{verb}' failed: {Describe()}", ex);
                    }
                }
                finally
                {
                    early.Dispose();
                }

                // Reported now, not after the pumping below: the caller reloads and unfreezes.
                // (Already reported as Invoked when the command took longer than InvokeReportDelay.)
                GiveForegroundBack(owner);
                if (cancelled)
                {
                    Complete(ShellMenuOutcome.Dismissed, verb, null);
                }
                else
                {
                    AppLog.Info($"Shell menu: '{verb}' on {_paths.Count} item(s), first {_paths[0]}");
                    Complete(ShellMenuOutcome.Invoked, verb, invokeError);
                }

                PumpAfterInvoke(dispatcher, owner);

                // A dialog of this thread that closed leaves the activation with the owner.
                GiveForegroundBack(owner);
            }
            finally
            {
                _menu2 = null;
                _menu3 = null;
                if (!menu.IsNull)
                {
                    PInvoke.DestroyMenu(menu);
                }

                bound.Release();
                if (!owner.IsNull)
                {
                    // Every way out (an unexpected failure too): the foreground must not stay with
                    // the invisible owner. Does nothing when the owner does not have it.
                    GiveForegroundBack(owner);
                    PInvoke.DestroyWindow(owner);
                }

                _current = null;
            }
        }

        /// <summary>
        /// Creates the PIDLs and the <c>IContextMenu</c> into <paramref name="bound"/>. Returns the
        /// reason when there is no menu (<c>bound.Menu</c> stays null).
        /// </summary>
        private unsafe string? Bind(HWND owner, Bound bound)
        {
            if (_background)
            {
                return BindBackground(owner, bound);
            }

            // One parent folder: its IShellFolder builds the menu, as in an Explorer window. Several
            // parents (a selection in 最近の履歴): an item array.
            (string Parent, IReadOnlyList<string> Paths)? group = ShellMenuTargets.TryGroup(_paths);
            IReadOnlyList<string> paths = group is { } grouped ? grouped.Paths : _paths;
            try
            {
                // Assigned before it is filled, so that a partly filled list is freed too.
                var pidls = new nint[paths.Count];
                bound.Pidls = pidls;
                for (int i = 0; i < pidls.Length; i++)
                {
                    ITEMIDLIST* pidl = PInvoke.ILCreateFromPath(ShellActions.ShellPath(paths[i]));
                    if (pidl is null)
                    {
                        return "項目が見つかりません。";
                    }

                    pidls[i] = (nint)pidl;
                }

                Guid menuId = typeof(IContextMenu).GUID;
                // The two calls are projected differently: IShellFolder.GetUIObjectOf hands back a
                // wrapper (out object), IShellItemArray.BindToHandler a raw pointer (out void*).
                object? menuObject = null;
                if (group is not null)
                {
                    Guid folderId = typeof(IShellFolder).GUID;
                    HRESULT bindResult = PInvoke.SHBindToParent((ITEMIDLIST*)pidls[0], &folderId, out object parent, null);
                    if (bindResult.Failed || parent is null)
                    {
                        return MessageOf(bindResult);
                    }

                    bound.Parent = parent;
                    var folder = (IShellFolder)parent;

                    // The last id of each absolute PIDL is the item relative to the parent; it points
                    // into the absolute PIDL, which lives until Release.
                    var children = new nint[pidls.Length];
                    for (int i = 0; i < children.Length; i++)
                    {
                        children[i] = (nint)PInvoke.ILFindLastID((ITEMIDLIST*)pidls[i]);
                    }

                    fixed (nint* first = children)
                    {
                        folder.GetUIObjectOf(owner, (uint)children.Length, (ITEMIDLIST**)first, &menuId, null, out object ui);
                        menuObject = ui;
                    }
                }
                else
                {
                    IShellItemArray array;
                    HRESULT arrayResult;
                    fixed (nint* first = pidls)
                    {
                        arrayResult = PInvoke.SHCreateShellItemArrayFromIDLists((uint)pidls.Length, (ITEMIDLIST**)first, out array);
                    }

                    if (arrayResult.Failed || array is null)
                    {
                        return MessageOf(arrayResult);
                    }

                    bound.Parent = array;
                    bound.FromItemArray = true;
                    Guid handler = PInvoke.BHID_SFUIObject;
                    array.BindToHandler(null, &handler, &menuId, out void* unknown);
                    if (unknown is not null)
                    {
                        try
                        {
                            menuObject = Marshal.GetObjectForIUnknown((nint)unknown);
                        }
                        finally
                        {
                            // The wrapper holds its own reference.
                            Marshal.Release((nint)unknown);
                        }
                    }
                }

                return SetMenu(bound, menuObject);
            }
            catch (Exception ex) when (IsBindFailure(ex))
            {
                AppLog.Warn($"Shell menu not available: {Describe()}", ex);
                return ex.Message;
            }
        }

        /// <summary>
        /// The background menu of the folder: its own <c>IShellFolder</c> (not the parent's) and
        /// <c>CreateViewObject</c>, as Explorer's view does for its blank area. <c>GetUIObjectOf</c>
        /// would give the folder's item menu instead. There is no view site, so the view's own
        /// entries (表示, 並べ替え, 最新の情報に更新) are not expected in it.
        /// </summary>
        private unsafe string? BindBackground(HWND owner, Bound bound)
        {
            try
            {
                ITEMIDLIST* pidl = PInvoke.ILCreateFromPath(ShellActions.ShellPath(_paths[0]));
                if (pidl is null)
                {
                    return "フォルダが見つかりません。";
                }

                bound.Pidls = [(nint)pidl];

                Guid folderId = typeof(IShellFolder).GUID;
                HRESULT bindResult = PInvoke.SHBindToObject(null, pidl, null, &folderId, out object folderObject);
                if (bindResult.Failed || folderObject is null)
                {
                    return MessageOf(bindResult);
                }

                bound.Parent = folderObject;
                Guid menuId = typeof(IContextMenu).GUID;
                // Projected with a raw pointer (out void*), like IShellItemArray.BindToHandler.
                ((IShellFolder)folderObject).CreateViewObject(owner, &menuId, out void* unknown);
                object? view = null;
                if (unknown is not null)
                {
                    try
                    {
                        view = Marshal.GetObjectForIUnknown((nint)unknown);
                    }
                    finally
                    {
                        // The wrapper holds its own reference.
                        Marshal.Release((nint)unknown);
                    }
                }

                return SetMenu(bound, view);
            }
            catch (Exception ex) when (IsBindFailure(ex))
            {
                AppLog.Warn($"Shell menu not available: {Describe()}", ex);
                return ex.Message;
            }
        }

        /// <summary>Puts the menu object into <paramref name="bound"/> (which then releases it).</summary>
        private static string? SetMenu(Bound bound, object? menuObject)
        {
            if (menuObject is null)
            {
                return "メニューを取得できませんでした。";
            }

            try
            {
                bound.Menu = (IContextMenu)menuObject;
            }
            catch (InvalidCastException)
            {
                // Not owned by bound yet: release the wrapper here.
                ShellContextMenu.Release(menuObject);
                throw;
            }

            return null;
        }

        private static bool IsBindFailure(Exception ex) =>
            ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException
                or IOException or NotImplementedException or NotSupportedException;

        private unsafe void Invoke(IContextMenu contextMenu, HWND owner, uint offset)
        {
            // MAKEINTRESOURCE(offset): the command is named by its offset from idCmdFirst.
            CMINVOKECOMMANDINFOEX info = default;
            info.cbSize = (uint)sizeof(CMINVOKECOMMANDINFOEX);
            info.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            info.hwnd = owner;
            info.lpVerb = (PCSTR)(byte*)(nuint)offset;
            info.lpVerbW = (char*)(nuint)offset;
            info.nShow = SW_SHOWNORMAL;
            info.ptInvoke = new System.Drawing.Point(_x, _y);
            contextMenu.InvokeCommand((CMINVOKECOMMANDINFO*)&info);
        }

        /// <summary>
        /// Gives the foreground to the main window, but only while the owner still has it: a window
        /// the command opened, or another application the user clicked, keeps it.
        /// </summary>
        private void GiveForegroundBack(HWND owner)
        {
            if (_mainWindow != 0 && (nint)PInvoke.GetForegroundWindow() == (nint)owner)
            {
                PInvoke.SetForegroundWindow(new HWND(_mainWindow));
            }
        }

        /// <summary>
        /// Lets the handlers fill and draw their own entries while the menu is tracked (送る,
        /// プログラムから開く, owner-drawn icons).
        /// </summary>
        /// <returns>
        /// True when the handler took <paramref name="msg"/>; <paramref name="value"/> is then the
        /// window procedure's result. False: default processing.
        /// </returns>
        private unsafe bool Hook(uint msg, WPARAM w, LPARAM l, out nint value)
        {
            value = 0;
            if (msg != WM_INITMENUPOPUP && msg != WM_DRAWITEM && msg != WM_MEASUREITEM && msg != WM_MENUCHAR)
            {
                return false;
            }

            if (msg == WM_INITMENUPOPUP && _appPopups.Contains(w.Value))
            {
                return false; // a submenu of the caller's entries: no handler filled it
            }

            try
            {
                if (_menu3 is { } menu3)
                {
                    LRESULT result = default;
                    menu3.HandleMenuMsg2(msg, w, l, &result);
                    value = result.Value;

                    // "Processed" is TRUE for these two; handlers often leave the result alone.
                    if (value == 0 && (msg == WM_DRAWITEM || msg == WM_MEASUREITEM))
                    {
                        value = 1;
                    }

                    return true;
                }

                if (_menu2 is { } menu2 && msg != WM_MENUCHAR)
                {
                    menu2.HandleMenuMsg(msg, w, l);
                    value = msg == WM_INITMENUPOPUP ? 0 : 1;
                    return true;
                }
            }
            catch (Exception)
            {
                // The handler does not take this message (E_NOTIMPL, E_FAIL…): default processing.
                // Nothing may escape into the window procedure.
            }

            value = 0;
            return false;
        }
    }
}
