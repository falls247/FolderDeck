using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The app's own right-click menu (§11), shared by cards, overlay rows, flyout rows, headers and
/// sidebar rows. The entries of an item are built once (<see cref="MenuEntry"/>) and shown either as
/// a WPF menu or, with the setting 「Windows のメニュー」, on top of Explorer's own menu. The blank
/// area of the item list gets the menu of the folder shown the same way (on top of Explorer's
/// background menu of that folder).
/// </summary>
public sealed class ContextMenuFactory
{
    private readonly MainViewModel _vm;

    public ContextMenuFactory(MainViewModel vm)
    {
        _vm = vm;
    }

    /// <summary>Raised when a menu opens / closes (the cascade stays open meanwhile, §9.6 rule 8).</summary>
    public event Action? MenuOpened;

    public event Action? MenuClosed;

    /// <summary>Called before a menu command runs that leaves the cascade (open, navigate…).</summary>
    public Action? BeforeNavigate { get; set; }

    /// <summary>名前の変更 of a card: the window opens the card's edit box (F2 does the same).</summary>
    public Action<CardViewModel>? RenameRequested { get; set; }

    /// <summary>新しいフォルダー of the blank area: the window creates it and opens its edit box (Ctrl+Shift+N does the same).</summary>
    public Action? NewFolderRequested { get; set; }

    /// <summary>
    /// Explorer's own menu opens (true) or has closed (false). True is raised synchronously, before
    /// the menu is built: from the Click of 「その他のメニュー（Windows）」, or, with the setting
    /// 「Windows のメニュー」, from the right click itself (<see cref="Show"/>). False follows every
    /// outcome (also a menu that never showed or timed out), before the result is acted on (also
    /// before a picked app entry runs or the WPF menu is shown instead).
    /// </summary>
    public event Action<bool>? NativeMenuChanged;

    /// <summary>Explorer menus asked for and not finished yet (normally 0 or 1).</summary>
    private int _nativeMenus;

    /// <summary>How long a path whose Explorer menu timed out gets the WPF menu directly (「Windows のメニュー」).</summary>
    private static readonly TimeSpan TimedOutSkip = TimeSpan.FromMinutes(5);

    /// <summary>Paths whose Explorer menu timed out, with Environment.TickCount64 at the timeout (UI thread only).</summary>
    private readonly Dictionary<string, long> _timedOut = new(PathTools.Comparer);

    /// <summary>
    /// As <see cref="_timedOut"/>, for folders whose background menu timed out. Kept apart: the item
    /// menu of a folder and its background menu are built by different extensions (Directory and
    /// Directory\Background), so one timing out says nothing about the other.
    /// </summary>
    private readonly Dictionary<string, long> _timedOutBackground = new(PathTools.Comparer);

    /// <summary>
    /// The menu of one item, or with <paramref name="group"/> (more than one item: the selection the
    /// clicked item is part of, in display order) the short menu of the whole group: 切り取り,
    /// コピー, パスをコピー and 削除. The caller decides the group (cascade rows and headers have none).
    /// With the setting 「Windows のメニュー」 an item Explorer's menu can be shown for gets that menu
    /// with these entries on top (built off the UI thread; the cascade is frozen meanwhile).
    /// </summary>
    public void Show(ItemRef item, FrameworkElement target, IReadOnlyList<ItemRef>? group = null)
    {
        // Where Explorer's menu will appear, and whether it gets the extended entries: read now, at
        // the right click, not when its item is clicked.
        var at = NativeMenuPoint.Capture(target);
        var entries = group is { Count: > 1 }
            ? GroupEntries(group, out var nativePaths, out bool nativeEnabled)
            : ItemEntries(item, out nativePaths, out nativeEnabled);
        ShowEntries(entries, target, at, nativePaths, nativeEnabled);
    }

    /// <summary>
    /// The entries of one item. <paramref name="nativePaths"/>: what Explorer's menu is for (null
    /// for a drive: its menu would offer フォーマット); <paramref name="nativeEnabled"/>: whether it
    /// can be shown (a usable item that is not a root).
    /// </summary>
    private List<MenuEntry> ItemEntries(ItemRef item, out IReadOnlyList<string>? nativePaths, out bool nativeEnabled)
    {
        var card = item.Card;

        // The 登録フォルダ row of the sidebar this menu is for (null for cards, rows and headers).
        var registered = item.Context == ItemContext.Sidebar ? _vm.FindRegistered(item.Path) : null;
        bool disabled = card?.IsDisabled == true || registered?.IsDisabled == true || item.IsDisabled;

        // A drive of the PC section: open, Explorer, copy the path, register. A drive is not pinned,
        // deleted or unregistered. One that is not connected can still be shown in Explorer (which
        // offers to reconnect it) and have its path copied; only opening it here is off.
        bool drive = item.Context == ItemContext.Drive;
        bool topCard = card?.IsTopLevel == true;
        var entries = new List<MenuEntry>();

        // The default entry of Explorer's menu (in place of the shell's open).
        entries.Add(new("開く", OpenGlyph, !disabled, () =>
        {
            BeforeNavigate?.Invoke();
            _ = card is not null ? _vm.ActivateCardAsync(card) : _vm.OpenItemAsync(item);
        }, IsDefault: true));
        entries.Add(new("エクスプローラーで表示", ExplorerGlyph, !disabled || drive, () => _ = _vm.ShowInExplorerAsync(item)));
        entries.Add(new("パスをコピー", CopyGlyph, !disabled || drive, () => _vm.CopyPath(item)));
        AddClipboardEntries(entries, [item], item);

        bool pinned = _vm.IsPinned(item.Path);
        bool canRegister = item.IsFolder && item.Context != ItemContext.Sidebar && !_vm.IsRegistered(item.Path);
        if (!drive || canRegister)
        {
            entries.Add(MenuEntry.Separator);
        }

        if (!drive)
        {
            entries.Add(new(pinned ? "ピン留めを外す" : "ピン留めする", pinned ? UnpinGlyph : PinGlyph, !disabled, () => _vm.TogglePin(item)));
        }

        if (canRegister)
        {
            // Any folder that is not on the sidebar yet (a card of 最近の履歴 / よく使う項目, a card or
            // row inside a folder, a flyout header) can be put there.
            entries.Add(new("登録フォルダに追加", AddGlyph, !disabled, () => _ = _vm.AddToRegisteredAsync(item)));
        }

        if (MainViewModel.CanDelete(item))
        {
            entries.Add(MenuEntry.Separator);
            entries.Add(new("削除（ごみ箱へ移動）", DeleteGlyph, true, () =>
            {
                BeforeNavigate?.Invoke(); // the confirmation and the reload replace the cascade
                _ = _vm.DeleteAsync([item]);
            }));

            // Only a card of the tab has an edit box (not a row of the cascade).
            if (card is not null && MainViewModel.CanRename(item))
            {
                entries.Add(new("名前の変更", RenameGlyph, true, () =>
                {
                    BeforeNavigate?.Invoke(); // the edit box needs the items uncovered
                    RenameRequested?.Invoke(card);
                }));
            }
        }

        if (registered is not null)
        {
            // Also for a missing folder: that is how it gets off the sidebar.
            entries.Add(MenuEntry.Separator);
            entries.Add(new("登録を解除", RemoveGlyph, true, () =>
            {
                BeforeNavigate?.Invoke();
                _vm.Unregister(registered);
            }));
        }

        if (topCard && card is not null)
        {
            switch (card.OwnerTab)
            {
                case TabKind.Recent:
                    entries.Add(MenuEntry.Separator);
                    entries.Add(new("履歴から外す", RemoveGlyph, true, () => _vm.ExcludeFromRecent(card)));
                    break;
                case TabKind.Frequent when !pinned:
                    entries.Add(MenuEntry.Separator);
                    entries.Add(new("一覧から外す", RemoveGlyph, true, () => _vm.RemoveFromFrequent(card)));
                    break;
            }
        }

        // Not on a drive row: a drive's menu would offer フォーマット.
        nativePaths = drive ? null : [item.Path];
        nativeEnabled = !disabled && TransferRules.IsTransferable(item.Path);
        return entries;
    }

    /// <summary>
    /// Several selected items: only what applies to all of them at once (切り取り, コピー, パスをコピー,
    /// 削除). No default entry: Explorer's menu keeps the shell's open (the app cannot open several).
    /// </summary>
    private List<MenuEntry> GroupEntries(IReadOnlyList<ItemRef> group, out IReadOnlyList<string>? nativePaths, out bool nativeEnabled)
    {
        var entries = new List<MenuEntry>();
        AddClipboardEntries(entries, group, pasteItem: null, separator: false);
        entries.Add(new("パスをコピー", CopyGlyph, true, () => _vm.CopyPaths(group)));
        if (group.All(MainViewModel.CanDelete))
        {
            // One confirmation and one Recycle Bin operation for the whole selection.
            entries.Add(MenuEntry.Separator);
            entries.Add(new("削除（ごみ箱へ移動）", DeleteGlyph, true, () =>
            {
                BeforeNavigate?.Invoke(); // the confirmation and the reload replace the cascade
                _ = _vm.DeleteAsync(group);
            }));
        }

        // Explorer's menu of the whole selection (one menu for every item, as with a selection in
        // Explorer): only when every item is usable and not a root.
        nativePaths = group.Select(i => i.Path).ToList();
        nativeEnabled = group.All(MainViewModel.CanCopy);
        return entries;
    }

    /// <summary>
    /// Explorer's menu with the entries on top when the setting asks for it and the items allow it;
    /// else the WPF menu (with 「その他のメニュー（Windows）」 last in the app's mode only). With
    /// <paramref name="background"/> Explorer's menu is the background menu of the one folder in
    /// <paramref name="nativePaths"/> (the blank area), not the menu of those items.
    /// </summary>
    private void ShowEntries(
        List<MenuEntry> entries, FrameworkElement target, NativeMenuPoint at, IReadOnlyList<string>? nativePaths, bool nativeEnabled,
        bool background = false)
    {
        bool windowsMenu = _vm.UsesWindowsItemMenu;
        if (nativePaths is not null && nativeEnabled && windowsMenu && !TimedOutRecently(nativePaths, background))
        {
            if (_nativeMenus > 0)
            {
                // A second right click while Explorer's menu is being built: the shell would answer
                // 「別のメニューを表示中」 and the WPF menu would open on top of the native one.
                return;
            }

            _ = ShowNativeAsync(nativePaths, at, entries, target, background);
            return;
        }

        var menu = Render(entries, target);
        if (nativePaths is not null && !windowsMenu)
        {
            AddNativeItem(menu, nativePaths, nativeEnabled, at, background);
        }

        Open(menu);
    }

    /// <summary>The WPF menu of <paramref name="entries"/> (IsDefault is not shown there), submenus included.</summary>
    private static ContextMenu Render(IReadOnlyList<MenuEntry> entries, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        foreach (var entry in entries)
        {
            menu.Items.Add(RenderEntry(entry));
        }

        return menu;
    }

    private static object RenderEntry(MenuEntry entry)
    {
        if (entry.IsSeparator)
        {
            return new Separator();
        }

        if (!entry.IsSubmenu)
        {
            return Item(entry.Header!, entry.Glyph, entry.Enabled, entry.Action!);
        }

        // No Click here: Click bubbles, a child's would run it too. The template shows ▶ and the submenu.
        var parent = new MenuItem { Header = entry.Header, Tag = entry.Glyph, IsEnabled = entry.Enabled };
        foreach (var child in entry.Children!)
        {
            parent.Items.Add(RenderEntry(child));
        }

        return parent;
    }

    /// <summary>
    /// The entries depth first for Explorer's menu, each with the index of the submenu entry it is in
    /// (-1: the top). <see cref="ShellMenuResult.AppIndex"/> counts in this list.
    /// </summary>
    private static List<(MenuEntry Entry, int Parent)> Flatten(IReadOnlyList<MenuEntry> entries)
    {
        var flat = new List<(MenuEntry Entry, int Parent)>();
        void Add(IReadOnlyList<MenuEntry> list, int parent)
        {
            foreach (var entry in list)
            {
                int index = flat.Count;
                flat.Add((entry, parent));
                if (entry.IsSubmenu)
                {
                    Add(entry.Children!, index);
                }
            }
        }

        Add(entries, -1);
        return flat;
    }

    /// <summary>
    /// The menu of the blank area of the item list: the menu of the folder shown, as Explorer's
    /// blank area. 貼り付け (into that folder) and 最新の情報に更新, then エクスプローラーで表示,
    /// パスをコピー and 登録フォルダに追加 for the folder itself, and Explorer's background menu of it
    /// (新規作成, プロパティ…): last as 「その他のメニュー（Windows）」, or with the setting
    /// 「Windows のメニュー」 below these entries. Without a folder on screen (the top lists, while it
    /// loads, a failed load) only 貼り付け (off) and 最新の情報に更新, in the WPF menu.
    /// </summary>
    public void ShowBlank(FrameworkElement target)
    {
        var at = NativeMenuPoint.Capture(target);
        var entries = BlankEntries(out var nativePaths);
        ShowEntries(entries, target, at, nativePaths, nativeEnabled: nativePaths is not null, background: true);
    }

    /// <summary>
    /// The entries of the blank area. <paramref name="nativePaths"/>: the folder shown, whose
    /// background menu Explorer's menu is (null without one). No default entry: a background has none.
    /// The actions keep the folder of the right click (RunEntry may run them after the menu closed).
    /// </summary>
    private List<MenuEntry> BlankEntries(out IReadOnlyList<string>? nativePaths)
    {
        string? folder = _vm.PasteFolderOf(null);
        var entries = new List<MenuEntry>
        {
            new("貼り付け", PasteGlyph, _vm.CanPaste(folder), () =>
            {
                BeforeNavigate?.Invoke(); // the transfer reloads the list
                _ = _vm.PasteAsync(folder!);
            }),
            MenuEntry.Separator,
            new("最新の情報に更新", RefreshGlyph, true, () => _vm.RefreshCommand.Execute(null)),
        };

        var shown = _vm.ShownFolderItem();
        if (shown is not null)
        {
            // Explorer's Ctrl+Shift+N: the new folder opens its edit box (the shell's 新規作成 cannot,
            // without a view of its own to edit in).
            entries.Insert(1, new("新しいフォルダー", NewFolderGlyph, !_vm.IsFileOperationRunning, () =>
            {
                BeforeNavigate?.Invoke(); // the reload replaces the cascade
                NewFolderRequested?.Invoke();
            }));
            entries.Add(MenuEntry.Separator);
            entries.Add(new("エクスプローラーで表示", ExplorerGlyph, true, () => _ = _vm.ShowInExplorerAsync(shown)));
            entries.Add(new("パスをコピー", CopyGlyph, true, () => _vm.CopyPath(shown)));
            if (!_vm.IsRegistered(shown.Path))
            {
                entries.Add(new("登録フォルダに追加", AddGlyph, true, () => _ = _vm.AddToRegisteredAsync(shown)));
            }

            AddOpenInEntries(entries, shown.Path);
        }

        nativePaths = shown is null ? null : [shown.Path];
        return entries;
    }

    /// <summary>
    /// 「ターミナルで開く」 (Windows Terminal, else cmd) and 「PowerShell で開く」 (pwsh, else Windows
    /// PowerShell), each with 開く / 管理者として開く, then 「Code で開く」 and 「WSL で開く」 when installed,
    /// for <paramref name="folder"/>. What is installed is the last look of <see cref="OpenInTools"/>,
    /// kept in the actions (RunEntry may run them after the menu closed).
    /// </summary>
    private void AddOpenInEntries(List<MenuEntry> entries, string folder)
    {
        var programs = OpenInTools.Current;
        OpenInTools.Refresh(); // the next right click sees an install / uninstall
        entries.Add(MenuEntry.Separator);
        entries.Add(Elevatable("ターミナルで開く", TerminalGlyph, OpenInTool.Terminal, folder, programs));
        entries.Add(Elevatable("PowerShell で開く", TerminalGlyph, OpenInTool.PowerShell, folder, programs));
        if (programs.Code is not null)
        {
            entries.Add(new("Code で開く", CodeGlyph, true, () => _ = _vm.OpenInAsync(OpenInTool.Code, elevated: false, folder, programs)));
        }

        if (programs.Wsl)
        {
            entries.Add(new("WSL で開く", TerminalGlyph, true, () => _ = _vm.OpenInAsync(OpenInTool.Wsl, elevated: false, folder, programs)));
        }
    }

    private MenuEntry Elevatable(string header, string glyph, OpenInTool tool, string folder, OpenInPrograms programs) =>
        new(header, glyph, true, null, Children:
        [
            new("開く", OpenGlyph, true, () => _ = _vm.OpenInAsync(tool, elevated: false, folder, programs)),
            new("管理者として開く", AdminGlyph, true, () => _ = _vm.OpenInAsync(tool, elevated: true, folder, programs)),
        ]);

    /// <summary>The menu of the PC section header: エクスプローラーで開く (This PC).</summary>
    public void ShowPcSection(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("エクスプローラーで開く", ExplorerGlyph, true, () => _ = _vm.OpenPcInExplorerAsync()));
        Open(menu);
    }

    /// <summary>
    /// The last item: 「その他のメニュー（Windows）」, Explorer's own menu for <paramref name="paths"/>
    /// (entries of shell extensions such as 7-Zip, 送る, プログラムから開く, プロパティ). The shell's
    /// delete, rename, cut, copy and paste are left out of it by <see cref="ShellContextMenu"/>
    /// (the app has its own; the shell's delete can be permanent). With <paramref name="background"/>
    /// it is the background menu of the folder in <paramref name="paths"/> (新規作成, プロパティ…).
    /// </summary>
    private void AddNativeItem(ContextMenu menu, IReadOnlyList<string> paths, bool enabled, NativeMenuPoint at, bool background = false)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("その他のメニュー（Windows）", NativeMenuGlyph, enabled, () => _ = ShowNativeAsync(paths, at, background: background)));
    }

    /// <summary>
    /// Explorer's menu for <paramref name="paths"/>; with <paramref name="app"/> (the setting
    /// 「Windows のメニュー」) those entries go on top of it, and a picked one runs here, on the UI
    /// thread. When Explorer's menu cannot be shown before it appears (unavailable, failed before
    /// showing, or timed out while being built), the WPF menu of <paramref name="app"/> opens at the
    /// same point instead; after a timeout with a short notice, and those paths then get the WPF
    /// menu directly for <see cref="TimedOutSkip"/>. With <paramref name="background"/> it is the
    /// background menu of the one folder in <paramref name="paths"/>.
    /// </summary>
    private async Task ShowNativeAsync(
        IReadOnlyList<string> paths, NativeMenuPoint at, IReadOnlyList<MenuEntry>? app = null, FrameworkElement? target = null,
        bool background = false)
    {
        // Synchronously, before anything is awaited: the cascade must be frozen before the menu
        // thread can take the foreground (the window's Deactivated would close it).
        if (_nativeMenus++ == 0)
        {
            NativeMenuChanged?.Invoke(true);
        }

        var flat = app is null ? null : Flatten(app); // submenus as entries with a Parent: AppIndex counts in it
        ShellMenuResult result;
        try
        {
            var appEntries = flat?.Select(f => new ShellMenuAppEntry(f.Entry.Header ?? "", f.Entry.Enabled, f.Entry.IsSeparator, f.Entry.IsDefault, f.Parent, f.Entry.IsSubmenu)).ToArray();
            nint mainWindow = _vm.View?.WindowHandle ?? 0;
            result = await (background
                ? ShellContextMenu.ShowBackgroundAsync(paths[0], at.X, at.Y, at.Extended, mainWindow, appEntries)
                : ShellContextMenu.ShowAsync(paths, at.X, at.Y, at.Extended, mainWindow, appEntries));
        }
        catch (Exception ex)
        {
            result = new ShellMenuResult(ShellMenuOutcome.Failed, null, ex.Message); // not expected: the task never faults
        }
        finally
        {
            if (--_nativeMenus == 0)
            {
                NativeMenuChanged?.Invoke(false);
            }
        }

        if (app is not null)
        {
            // After the unfreeze, as a WPF entry's Click: its BeforeNavigate closes the cascade.
            if (result.Outcome == ShellMenuOutcome.AppCommand)
            {
                if (result.AppIndex >= 0 && result.AppIndex < flat!.Count
                    && flat[result.AppIndex].Entry is { IsSeparator: false, Enabled: true, Action: not null } entry)
                {
                    RunEntry(entry);
                }

                return;
            }

            if (result.Outcome == ShellMenuOutcome.TimedOut)
            {
                // Never shown and it will not appear later (the stage is cancelled before
                // TrackPopupMenuEx). The paths skip Explorer's menu for a while, so that each right
                // click does not freeze the cascade again and leave one more hung thread.
                AppLog.Info("Explorer's menu timed out, the app's menu instead");
                RememberTimedOut(paths, background);
                _vm.Message.Info("エクスプローラーのメニューが時間内に準備できなかったため、アプリのメニューを出しました。");
                OpenFallback(app, target, at);
                return;
            }

            if (result.Outcome == ShellMenuOutcome.Unavailable
                || (result.Outcome == ShellMenuOutcome.Failed && !result.Shown))
            {
                // Nothing appeared: the user still gets a menu, without a warning.
                AppLog.Info($"Explorer's menu not shown ({result.Error}), the app's menu instead");
                OpenFallback(app, target, at);
                return;
            }
        }

        // After the unfreeze: the reload after a command closes the cascade, which a frozen one ignores.
        await _vm.OnShellMenuClosedAsync(result);
    }

    /// <summary>
    /// Runs an entry picked in Explorer's menu. Outside a Click, an exception would not reach the
    /// dispatcher's handler but vanish in the discarded task.
    /// </summary>
    private void RunEntry(MenuEntry entry)
    {
        try
        {
            entry.Action!();
        }
        catch (Exception ex)
        {
            AppLog.Error("App menu entry failed", ex);
            _vm.Message.Error("操作できませんでした。");
        }
    }

    private void RememberTimedOut(IReadOnlyList<string> paths, bool background)
    {
        var timedOut = background ? _timedOutBackground : _timedOut;
        long now = Environment.TickCount64;
        foreach (var path in paths)
        {
            timedOut[path] = now;
        }
    }

    /// <summary>
    /// Whether one of <paramref name="paths"/> timed out within <see cref="TimedOutSkip"/> (expired
    /// entries are dropped, so a shell extension that was only slow at startup gets another try).
    /// </summary>
    private bool TimedOutRecently(IReadOnlyList<string> paths, bool background)
    {
        var timedOut = background ? _timedOutBackground : _timedOut;
        if (timedOut.Count == 0)
        {
            return false;
        }

        long now = Environment.TickCount64;
        foreach (var expired in timedOut.Where(p => now - p.Value >= (long)TimedOutSkip.TotalMilliseconds).Select(p => p.Key).ToList())
        {
            timedOut.Remove(expired);
        }

        return paths.Any(timedOut.ContainsKey);
    }

    /// <summary>
    /// The WPF menu of <paramref name="entries"/> at the screen point of the right click (without
    /// 「その他のメニュー（Windows）」), opened at once, without an await after the unfreeze.
    /// </summary>
    private void OpenFallback(IReadOnlyList<MenuEntry> entries, FrameworkElement? target, NativeMenuPoint at)
    {
        FrameworkElement? anchor = target is { IsVisible: true } && PresentationSource.FromVisual(target) is not null
            ? target
            : Application.Current.MainWindow;
        if (anchor is null || PresentationSource.FromVisual(anchor) is null)
        {
            return;
        }

        var menu = Render(entries, anchor);
        menu.Placement = PlacementMode.Relative;

        // Physical pixels to DIP of the anchor.
        var offset = anchor.PointFromScreen(new Point(at.X, at.Y));
        menu.HorizontalOffset = offset.X;
        menu.VerticalOffset = offset.Y;
        Open(menu);
    }

    /// <summary>
    /// 切り取り and コピー when every item allows them, and 貼り付け into <paramref name="pasteItem"/>
    /// when that is a folder (or a usable drive row), enabled while the clipboard offers files.
    /// </summary>
    private void AddClipboardEntries(List<MenuEntry> entries, IReadOnlyList<ItemRef> items, ItemRef? pasteItem, bool separator = true)
    {
        bool cut = items.All(MainViewModel.CanCut);
        bool copy = items.All(MainViewModel.CanCopy);
        string? pasteFolder = pasteItem is null ? null : _vm.PasteFolderOf(pasteItem);
        if (!cut && !copy && pasteFolder is null)
        {
            return;
        }

        if (separator)
        {
            entries.Add(MenuEntry.Separator);
        }

        if (cut)
        {
            entries.Add(new("切り取り", CutGlyph, true, () => _vm.CopyToClipboard(items, cut: true)));
        }

        if (copy)
        {
            entries.Add(new("コピー", CopyGlyph, true, () => _vm.CopyToClipboard(items, cut: false)));
        }

        if (pasteFolder is not null)
        {
            entries.Add(new("貼り付け", PasteGlyph, _vm.CanPaste(pasteFolder), () =>
            {
                BeforeNavigate?.Invoke(); // the transfer reloads the list the cascade hangs on
                _ = _vm.PasteAsync(pasteFolder);
            }));
        }
    }

    private void Open(ContextMenu menu)
    {
        menu.Opened += (_, _) => MenuOpened?.Invoke();
        menu.Closed += (_, _) => MenuClosed?.Invoke();
        menu.IsOpen = true;
    }

    // Segoe Fluent Icons (named in the comments where the constant's name does not say it).
    private const string OpenGlyph = "\uE8E5"; // Open
    private const string PinGlyph = "\uE718"; // Pin
    private const string UnpinGlyph = "\uE77A"; // Unpin
    private const string AddGlyph = "\uE710"; // Add
    private const string RemoveGlyph = "\uE711"; // Cancel (remove from a list)
    private const string CutGlyph = "\uE8C6";
    private const string CopyGlyph = "\uE8C8";
    private const string PasteGlyph = "\uE77F";
    private const string RefreshGlyph = "\uE72C";
    private const string DeleteGlyph = "\uE74D"; // Delete
    private const string RenameGlyph = "\uE8AC"; // Rename
    private const string NewFolderGlyph = "\uE8F4"; // NewFolder
    private const string ExplorerGlyph = "\uEC50"; // File Explorer
    private const string NativeMenuGlyph = "\uE712"; // More (…)
    private const string TerminalGlyph = "\uE756"; // CommandPrompt
    private const string CodeGlyph = "\uE943"; // Code
    private const string AdminGlyph = "\uEA18"; // Shield

    /// <summary>
    /// One entry of an item's menu, rendered as a WPF <see cref="MenuItem"/> or handed to Explorer's
    /// menu (text only). A null <see cref="Header"/> is a separator. <see cref="IsDefault"/> is the
    /// default (bold) entry of Explorer's menu (top level only). With <see cref="Children"/> it is a
    /// submenu (▶): <see cref="Action"/> is null, the children run.
    /// </summary>
    private sealed record MenuEntry(
        string? Header, string Glyph, bool Enabled, Action? Action, bool IsDefault = false, IReadOnlyList<MenuEntry>? Children = null)
    {
        public static MenuEntry Separator { get; } = new(null, "", false, null);

        public bool IsSeparator => Header is null;

        public bool IsSubmenu => Children is { Count: > 0 };
    }

    /// <summary>The pointer in screen pixels and the Shift state when the app's menu was asked for.</summary>
    private readonly record struct NativeMenuPoint(int X, int Y, bool Extended)
    {
        public static NativeMenuPoint Capture(FrameworkElement target)
        {
            bool extended = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ShellInterop.TryGetCursorPos(out int x, out int y))
            {
                return new NativeMenuPoint(x, y, extended);
            }

            // Fallback: the target's top-left corner (PointToScreen gives device pixels).
            var corner = target.IsVisible ? target.PointToScreen(new Point(0, 0)) : default;
            return new NativeMenuPoint((int)corner.X, (int)corner.Y, extended);
        }
    }

    private static MenuItem Item(string header, string glyph, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, Tag = glyph, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
}
