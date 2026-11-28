using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Selection;
using FolderDeck.Core.Stores;
using FolderDeck.Core.Transfer;
using FolderDeck.Shell;
using FolderDeck.ViewModels;
using Microsoft.Win32;

namespace FolderDeck.Views;

public partial class MainWindow : Window, IMainView, ICardHost
{
    /// <summary>Check mark of the selected menu item (Segoe Fluent Icons).</summary>
    private const string CheckGlyph = "";

    private readonly MainViewModel _vm;
    private readonly ThemeManager _theme;
    private readonly CascadeController _cascade;
    private readonly ContextMenuFactory _menus;

    /// <summary>The edit box of an item's name (F2, 名前の変更, a new folder).</summary>
    private readonly RenameEditor _rename;

    /// <summary>
    /// The window was activated while a name was being edited (or a folder made for it): the
    /// activation's checks (<see cref="MainViewModel.OnActivatedAsync"/>) run once that is over,
    /// because their reload would end the edit box with a half-typed name.
    /// </summary>
    private bool _activationPending;

    /// <summary>Ctrl+Shift+N is making the folder whose edit box opens next: keys do nothing meanwhile, text is kept for the box.</summary>
    private bool _creatingFolder;

    /// <summary>
    /// A committed name is being handled (<see cref="CommitRenameAsync"/>: its confirmation, the
    /// shell, the reload, maybe the box again). Closing the confirmation activates the window: its
    /// reload must not end a box that opens again meanwhile.
    /// </summary>
    private bool _committingRename;

    /// <summary>
    /// The paths F2 renames together (several selected, display order) while the edit box is open;
    /// null for one item. Taken when the name is committed, dropped when the box is cancelled.
    /// </summary>
    private IReadOnlyList<string>? _renameBatch;

    private readonly System.Text.StringBuilder _typeAhead = new();

    /// <summary>
    /// Set when the sidebar was just shown or hidden: the toggle sits right of the sidebar and moves
    /// with it, so the further presses of a double click would land on whatever took its place.
    /// </summary>
    private bool _sidebarJustToggled;

    /// <summary>Window messages the app reacts to (a drive came or went); created once the window has its handle.</summary>
    private MainWindowHook? _hook;

    /// <summary>
    /// When the drives were last read because the window was activated (<see cref="Environment.TickCount64"/>).
    /// Starts at the creation of the window: the list is read at startup anyway.
    /// </summary>
    private long _drivesReadAt = Environment.TickCount64;

    public MainWindow(MainViewModel vm, ThemeManager theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        DataContext = vm;
        _menus = new ContextMenuFactory(vm);
        _cascade = new CascadeController(this, vm, _menus);
        vm.View = this;
        CardView.Host = this;

        // The edit box ends synchronously; the rename runs after the input that ended it (a press
        // elsewhere, a lost focus) has been dealt with, so its confirmation never opens inside a
        // mouse or focus event.
        _rename = new RenameEditor(this, text => vm.Message.Info(text));
        _rename.Ended += committed =>
        {
            _cascade.SetEditing(IsAddressEditing);
            if (!committed)
            {
                _renameBatch = null;
                RunPendingActivation();
            }
        };
        _rename.Committed += (card, text) =>
        {
            // Ended(true) comes first and keeps the batch: it is taken here.
            var batch = _renameBatch;
            _renameBatch = null;
            _committingRename = true; // before the dispatch: an activation may come before it runs
            Dispatcher.BeginInvoke(() => _ = CommitRenameAsync(card, text, batch));
        };
        _menus.RenameRequested = card => BeginRename(card);
        _menus.NewFolderRequested = () => _ = CreateFolderAndRenameAsync();

        // The stores are loaded before the window is created: the sidebar opens as it was saved
        // (shown at its width, or hidden).
        ApplySidebarLayout();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed))
            {
                ApplySidebarLayout();
                _sidebarJustToggled = true;
            }
            else if (e.PropertyName == nameof(MainViewModel.ShownLayout))
            {
                BuildPanes(); // announced before ActivePane: the views are there when it is
            }
            else if (e.PropertyName == nameof(MainViewModel.ActivePane))
            {
                MarkActivePane();
                EndAddressEdit(); // the path in the box is the other pane's
                WatchCrumbs(vm.ActivePane); // the other pane's crumbs replace these
            }
        };
        BuildPanes();
        WatchCrumbs(vm.ActivePane);

        // Windows' names of the details view's extra columns are read once off the UI thread (the
        // built-in titles show until then; NewPaneView gives them to every pane's list).
        _ = LoadColumnTitlesAsync();

        // A click in a cascade opened from an item of a pane that is not the active one: that pane
        // becomes active first, so that what the click does (navigate, open, the menu) acts on it.
        _cascade.ContentActivated = element => ActivatePaneOf(element);

        // A sidebar row is one element: the pointer rests on it and its list goes beside it.
        Sidebar.ItemEntered += (row, item) => _cascade.AnchorMouseEnter(row, row, item);
        Sidebar.ItemMoved += (row, item, position) => _cascade.AnchorMouseMove(row, row, item, position);
        Sidebar.ItemLeft += row => _cascade.AnchorMouseLeave(row);
        Sidebar.ItemClicked += OnSidebarClicked;
        Sidebar.ItemContextMenu += (item, row) => _menus.Show(item.ToItemRef(), row);
        Sidebar.PcHeaderContextMenu += OnPcHeaderContextMenu;
        Sidebar.ItemExplorerRequested += OnSidebarExplorer;
        Sidebar.ItemDragRequested += OnSidebarDrag;
        Sidebar.Scrolled += () => _cascade.CloseAll();
        vm.DriveList.RowRemoved += row => _cascade.AnchorRemoved(row);
        vm.DriveList.Drives.CollectionChanged += (_, _) =>
        {
            // A drive came or went above the row a list hangs on: the row moved, the list did not.
            if (vm.DriveList.Drives.Any(d => d.IsHoverHeld))
            {
                _cascade.CloseAll();
            }
        };
        TabStrip.Interacted += () => _cascade.CloseAll();

        // A tab that is not shown is an anchor too: resting on its header lists its folder under it (TabLayoutViewModel as ICascadeAnchor).
        TabStrip.TabEntered += (element, tab) => _cascade.AnchorMouseEnter(element, element, tab);
        TabStrip.TabMoved += (element, tab, position) => _cascade.AnchorMouseMove(element, element, tab, position);
        TabStrip.TabLeft += element => _cascade.AnchorMouseLeave(element);
        TabStrip.TabsShifted += () => _cascade.AnchorsMoved<TabLayoutViewModel>();
        TabStrip.TabDraggedOverPanes += OnTabDraggedOverPanes;
        TabStrip.TabDroppedOnPanes += OnTabDroppedOnPanes;
        Sidebar.PaneItemClicked += row =>
        {
            _cascade.CloseAll();
            _ = _vm.OpenRegisteredPaneAsync(row);
        };
        Sidebar.PaneItemContextMenu += (row, element) =>
        {
            _cascade.CloseAll();
            _menus.ShowRegisteredPane(row, element);
        };

        // §9.6 rule 5: these close every flyout at once (Popups are topmost windows).
        Deactivated += (_, _) => _cascade.CloseAll();
        LocationChanged += (_, _) => _cascade.CloseAll();
        SizeChanged += (_, _) => _cascade.CloseAll();
        StateChanged += (_, _) => _cascade.CloseAll();
        Application.Current.Deactivated += (_, _) => _cascade.CloseAll();

        SourceInitialized += (_, _) => _theme.ApplyTitleBar(this);
        SourceInitialized += (_, _) => WatchWindowMessages();
        Activated += OnActivatedReadDrives;
        Activated += (_, _) =>
        {
            // A cut of ours may have been pasted in Explorer. Not under a name being edited (see _activationPending).
            if (IsNameBusy)
            {
                _activationPending = true;
                return;
            }

            _ = vm.OnActivatedAsync();
        };
        PreviewTextInput += OnPreviewTextInput;
        Closed += (_, _) =>
        {
            _vm.StopAutoRefresh();
            _hook?.Dispose();
        };
        PreviewMouseWheel += (_, _) => NoteInput();

        // A pointer on its way to a click counts as input (the list must not move under it). Only a
        // real move: WPF raises a MouseMove of its own when the layout under a still pointer changes.
        PreviewMouseMove += (_, e) =>
        {
            var at = e.GetPosition(this);
            if (at != _lastPointer)
            {
                _lastPointer = at;
                NoteInput();

                // After a drop on a crumb WPF may never raise MouseEnter / MouseLeave for it (the OLE
                // drag hid the pointer from it): the first real move off the crumbs arms them again.
                if (_crumbQuiet && !IsOverCrumb(e.OriginalSource))
                {
                    _crumbQuiet = false;
                }
            }
        };
        theme.ThemeChanged += () => _theme.ApplyTitleBar(this);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseDown += OnPreviewMouseDown;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) =>
        {
            HideDropHint();
            SetDropMark(null);
            MarkDropView(null);
        };
        Drop += OnDrop;
        Closing += OnClosing;
        IsVisibleChanged += OnVisibleChanged;
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (_staleButton is not null && !ReferenceEquals(e.NewFocus, _staleButton))
            {
                _staleButton = null; // another element (a text box, a button clicked) has the focus now
            }
        };
        Closed += (_, _) => _closed = true;

        // The blank part of the item list: a press clears the selection, a right click offers paste.
        // The cards' own presses are not handled (ItemPointerTracker), and the scroll viewer may
        // mark a press handled when it takes the focus: handledEventsToo, then OriginalSource decides.
        ContentHost.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnContentLeftDown), handledEventsToo: true);
        ContentHost.MouseRightButtonUp += OnContentRightUp;
    }

    /// <summary>
    /// The window refuses to close silently while a paste copies or moves files or a delete moves
    /// them to the Recycle Bin: the operation runs on a background thread and would be cut off
    /// halfway. Asks first (default: キャンセル).
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 常駐モード: the user's close only hides (a program's WM_CLOSE, 終了 and the sign-out quit).
        if (!_exitRequested && !_sessionEnding && _vm.KeepRunningOnClose && _hook?.IsUserClose == true)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (!_sessionEnding && _vm.IsFileOperationRunning
            && !Confirm("終了しますか？", "ファイルの移動・コピー・削除が実行中です。終了すると途中で中断されます。", null, "終了する", danger: true))
        {
            e.Cancel = true;
            _exitRequested = false;
            return;
        }

        _vm.SaveState(CapturePlacement());
    }

    // ---- 常駐モード: hidden in the notification area ------------------------------------------

    /// <summary>Windows' names of the details view's extra columns, once read (null until then).</summary>
    private IReadOnlyDictionary<string, string>? _columnTitles;

    private async Task LoadColumnTitlesAsync()
    {
        try
        {
            _columnTitles = await _vm.Services.Properties.TitlesAsync();
            foreach (var view in _paneViews)
            {
                view.DetailsList.SetExtraTitles(_columnTitles);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The titles of the extra details columns could not be read", ex);
        }
    }

    /// <summary>The next close quits (終了 in the tray, a request of another program).</summary>
    private bool _exitRequested;

    /// <summary>Windows ends the session: nothing may refuse or ask.</summary>
    private bool _sessionEnding;

    /// <summary>Closed already (the exit event can come right after a CloseMainWindow of the same script).</summary>
    private bool _closed;

    /// <summary>Hidden (or never shown: --background): the next show resumes the checks and refreshes the shown tab.</summary>
    private bool _resumeOnShow;

    /// <summary>Started with --background: the handle exists (hooks, tray, session monitor), the window is not shown.</summary>
    public void PrepareHidden()
    {
        _resumeOnShow = true;
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>The window is shown cloaked once (laid out and drawn, nothing on screen) until <see cref="EndPrewarm"/>.</summary>
    private bool _prewarming;

    /// <summary>
    /// Started with --background, the first tab loaded: the window is laid out and drawn once while
    /// cloaked (no picture, no taskbar button), then hidden again. A window never drawn took about
    /// 400 ms to come up at the first reveal; after this it takes as long as the later ones.
    /// </summary>
    public void PrewarmHidden()
    {
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (IsVisible || !ShellInterop.SetCloaked(hwnd, true))
        {
            return;
        }

        _prewarming = true;
        bool activate = ShowActivated;
        ShowActivated = false;
        Show();
        ShowActivated = activate;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_prewarming)
            {
                Hide();
                EndPrewarm();
            }
        });
    }

    private void EndPrewarm()
    {
        _prewarming = false;
        ShellInterop.SetCloaked(new WindowInteropHelper(this).Handle, false);
    }

    /// <summary>Windows ends the session: the close must go through, without a question.</summary>
    public void AllowClose()
    {
        _sessionEnding = true;
        _exitRequested = true;
    }

    /// <summary>The state for an exit that WPF's shutdown may not reach (the session ends).</summary>
    public void SaveForExit() => _vm.SaveState(CapturePlacement());

    /// <summary>終了 in the tray, or another program asked to quit: closes for real (asks while a file operation runs).</summary>
    public void RequestExit(string by)
    {
        if (_closed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        AppLog.Info($"resident: exit requested by {by}");
        _exitRequested = true;
        if (_vm.IsFileOperationRunning && !IsVisible)
        {
            BringToFront(); // the question needs a visible owner
        }

        Close();
    }

    /// <summary>
    /// Shows the window (from hidden, minimized or behind others) and brings it to the front.
    /// <paramref name="askedAt"/>: when the request came (a second launch's signal), for the log.
    /// </summary>
    public void Reveal(string by, long? askedAt = null)
    {
        bool wasHidden = !IsVisible || _prewarming;
        long start = askedAt ?? Environment.TickCount64;
        if (_prewarming)
        {
            EndPrewarm(); // asked for while being drawn: it stays shown
            _resumeOnShow = false;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _ = _vm.OnWindowShownAsync());
        }

        BringToFront();
        if (wasHidden)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                AppLog.Info($"resident: shown from hidden by {by} in {Environment.TickCount64 - start} ms"));
        }
    }

    private void HideToTray()
    {
        _cascade.CloseAll();
        var placement = CapturePlacement();
        Hide();
        _resumeOnShow = true;
        _vm.OnWindowHidden(placement);
        AppLog.Info("resident: window hidden to the notification area");
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _resumeOnShow && !_prewarming)
        {
            _resumeOnShow = false;

            // After the first frame: the refresh must not delay the window coming up.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _ = _vm.OnWindowShownAsync());
        }
    }

    // ---- window placement (§5.5) ------------------------------------------------------------

    public void RestorePlacement(WindowPlacementSetting? placement)
    {
        placement ??= new WindowPlacementSetting();
        Width = placement.Width;
        Height = placement.Height;
        var work = SystemParameters.WorkArea; // primary monitor, DIP
        bool visible = false;
        if (placement.Left is double left && placement.Top is double top)
        {
            double scale = Math.Max(1.0, System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX);
            // The title bar strip must land on some monitor, or the window would be unreachable.
            visible = ShellInterop.IsTitleStripOnAnyMonitor(left * scale, top * scale, placement.Width * scale, scale);
            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (!visible)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Math.Min(Width, work.Width);
            Height = Math.Min(Height, work.Height);
            Left = work.Left + ((work.Width - Width) / 2);
            Top = work.Top + ((work.Height - Height) / 2);
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private WindowPlacementSetting CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowPlacementSetting
        {
            Left = double.IsFinite(bounds.Left) ? Math.Round(bounds.Left) : null,
            Top = double.IsFinite(bounds.Top) ? Math.Round(bounds.Top) : null,
            Width = double.IsFinite(bounds.Width) ? Math.Round(bounds.Width) : 1280,
            Height = double.IsFinite(bounds.Height) ? Math.Round(bounds.Height) : 800,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    /// <summary>A second instance asked us to come to the front (§4.4).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ShellInterop.BringToFront(hwnd);
        }

        Focus();
    }

    // ---- keyboard and mouse (§8.2) ----------------------------------------------------------

    /// <summary>
    /// The menu key (<see cref="Key.Apps"/> or <see cref="Key.F10"/> of Shift+F10) whose press opened
    /// the list's menu; <see cref="Key.None"/> otherwise. Its release is swallowed too.
    /// </summary>
    private Key _menuKeyDown;

    /// <summary>
    /// The release of a menu key whose press the list used: neither WPF's keyboard context menu
    /// (raised on the release of Shift+F10 / Apps for the focused element) nor DefWindowProc
    /// (WM_SYSKEYUP of F10: the system menu's menu mode; WM_KEYUP of Apps: WM_CONTEXTMENU) gets it.
    /// Not reached while the focus is inside the menu that opened (a popup); nothing there acts on it.
    /// </summary>
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_menuKeyDown != Key.None && key == _menuKeyDown)
        {
            _menuKeyDown = Key.None;
            e.Handled = true;
        }

        if (_altDropUp && key is Key.LeftAlt or Key.RightAlt)
        {
            // The Alt of an Alt + drop (a shortcut) is released after the drop: unhandled, its
            // WM_SYSKEYUP reaches DefWindowProc, which enters the system menu's mode, and the next
            // press only leaves that mode (the next drag did not start).
            _altDropUp = false;
            e.Handled = true;
        }
    }

    /// <summary>An Alt + drop just made shortcuts: the release of its Alt is swallowed (<see cref="OnPreviewKeyUp"/>).</summary>
    private bool _altDropUp;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        NoteInput();
        if (e.Key == Key.Tab)
        {
            _staleButton = null; // Tab goes to the controls on purpose: a button it lands on takes Space again
        }
        if (_menuKeyDown != Key.None
            && (e.Key == Key.System ? e.SystemKey : e.Key) is var pressed
            && pressed != _menuKeyDown && pressed is not (Key.LeftShift or Key.RightShift))
        {
            _menuKeyDown = Key.None; // its release went to the menu's popup: a later one is not ours
        }

        if (ActiveList.IsMarqueeRunning)
        {
            // Esc puts the selection back; no other key may reload or change the list under the
            // box (F5, Ctrl+W, Delete, Ctrl+A, Backspace…). Alt+F4 still closes the window.
            if (e.Key == Key.Escape)
            {
                ActiveList.CancelMarquee();
            }

            e.Handled = !(e.Key == Key.System && e.SystemKey == Key.F4);
            return;
        }

        if (_rename.IsOpen)
        {
            // Every key belongs to the name's edit box (Enter, Esc and the text): no F5, Ctrl+W,
            // Alt+← or cascade key reloads or leaves the list from under it.
            return;
        }

        if (_creatingFolder)
        {
            // The new folder's edit box is about to open: typing goes to it (OnPreviewTextInput),
            // Backspace takes back what was typed, and no key navigates or deletes meanwhile.
            // Letters are not marked handled: that would drop their text input.
            if (e.Key == Key.Back && _typeAhead.Length > 0)
            {
                _typeAhead.Length--;
            }

            e.Handled = e.Key is Key.Enter or Key.Back;
            return;
        }

        bool inTextBox = Keyboard.FocusedElement is TextBox;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
        {
            _cascade.CloseForSelectionKeys(); // a Ctrl / Shift click is coming: nothing may cover the items
            return;
        }

        if (_cascade.IsOpen && (!inTextBox || key == Key.Escape) && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_cascade.HandleKey(key))
            {
                e.Handled = true;
                return;
            }
        }

        if (!inTextBox && HandleSelectionKey(key, e))
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Space && _staleButton is not null && ReferenceEquals(Keyboard.FocusedElement, _staleButton))
        {
            e.Handled = true; // nothing in the list took it (no cursor yet): still not the stale button's
            return;
        }

        switch (key)
        {
            case Key.F5:
                e.Handled = true;
                EndAddressEdit(); // not open over a reload of the whole list (see MainWindow.xaml)
                _vm.RefreshCommand.Execute(null);
                break;
            case Key.Back when !inTextBox && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                _vm.GoUpCommand.Execute(null);
                break;
            case Key.L when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
            case Key.D when Keyboard.Modifiers == ModifierKeys.Alt && _vm.HasTab:
            case Key.F4 when Keyboard.Modifiers == ModifierKeys.None && _vm.HasTab:
                e.Handled = true; // like Explorer: the address bar's path, selected
                BeginAddressEdit();
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
            case Key.E when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
            case Key.F3 when Keyboard.Modifiers == ModifierKeys.None && _vm.HasTab:
                e.Handled = true; // like Explorer: the search box, its text selected
                BeginSearch();
                break;
            case Key.Tab when (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.Control && _vm.Tabs.Count > 0 && !IsMenuTakingKeys():
                // Also in the search / address box, as in Explorer. Handled here, at the window's
                // preview, before WPF's focus traversal would move the focus (Ctrl+Tab's old effect).
                e.Handled = true;
                EndAddressEdit(); // the box shows the tab left (the search box closes with the tab)
                _vm.SelectAdjacentTab(back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
            case >= Key.D1 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.Control && _vm.Tabs.Count > 0 && !IsMenuTakingKeys():
            case >= Key.NumPad1 and <= Key.NumPad9 when Keyboard.Modifiers == ModifierKeys.Control && _vm.Tabs.Count > 0 && !IsMenuTakingKeys():
                e.Handled = true; // Ctrl+1…8: that tab; Ctrl+9: the last one (as Explorer)
                EndAddressEdit();
                int digit = key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1;
                _vm.SelectTabAt(digit == 8 ? int.MaxValue : digit);
                break;
            case Key.F1 or Key.OemQuestion or Key.Divide when ShortcutHelpWindow.IsToggleKey(e) && !IsMenuTakingKeys():
                // §3.3-2: the shortcut list. Also in the search / address box (neither key types text);
                // not while a name is edited (that branch returned above). The list closes itself.
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    ShowShortcutHelp();
                }

                break;
            case Key.W when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasPanes:
                e.Handled = true; // like Explorer: closes the shown tab (in a split, the active pane's tab), whatever its kind; an empty pane closes
                if (_vm.HasTab)
                {
                    _vm.CloseTabCommand.Execute(_vm.SelectedTab);
                }
                else
                {
                    _vm.CloseActivePaneCommand.Execute(null); // an empty pane; the blank page of ＋ goes back to the tab before it
                }

                break;
            case Key.Oem5 or Key.OemBackslash when (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.Control && _vm.HasPanes && !IsMenuTakingKeys():
                // Ctrl + \ (the ¥ key and the ろ key of a Japanese keyboard, \ of others): left | right;
                // with Shift, top / bottom. Also in the search / address box (neither types with Ctrl).
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    EndAddressEdit();
                    var axis = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? SplitAxis.Rows : SplitAxis.Columns;
                    if (_vm.SplitPaneCommand.CanExecute(axis))
                    {
                        _vm.SplitPaneCommand.Execute(axis); // as the toolbar's buttons: at the pane limit, closes the pane beside
                    }
                }

                break;
            case Key.F6 when (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None && _vm.HasPanes && !IsMenuTakingKeys():
                // the next / previous pane of the split (as Windows Terminal's focus moves); nothing otherwise
                if (_vm.IsSplit)
                {
                    e.Handled = true;
                    EndAddressEdit();
                    _vm.SelectAdjacentPane(back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                }

                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && _vm.IsSplit && !IsMenuTakingKeys():
                // the pane that way (Windows Terminal's Alt + arrow; here with Shift: Alt + ← → ↑ ↓ are back / forward / up / into)
                e.Handled = true;
                EndAddressEdit();
                _vm.MoveToPane(key switch
                {
                    Key.Left => PaneDirection.Left,
                    Key.Right => PaneDirection.Right,
                    Key.Up => PaneDirection.Up,
                    _ => PaneDirection.Down,
                });
                break;
            case Key.T when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !IsMenuTakingKeys():
                e.Handled = true; // as the browsers: the last closed tab comes back (also with no tab open)
                if (!e.IsRepeat)
                {
                    EndAddressEdit();
                    _vm.ReopenClosedTabCommand.Execute(null);
                }

                break;
            case Key.N when !inTextBox && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true; // like Explorer: a new folder in the folder shown, in its edit box
                if (!e.IsRepeat)
                {
                    _ = CreateFolderAndRenameAsync();
                }

                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt:
            case Key.BrowserBack:
                e.Handled = true;
                _vm.GoBackCommand.Execute(null);
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Alt:
            case Key.BrowserForward:
                e.Handled = true;
                _vm.GoForwardCommand.Execute(null);
                break;
            case Key.Up when !inTextBox && Keyboard.Modifiers == ModifierKeys.Alt && !IsMenuTakingKeys():
                e.Handled = true; // like Explorer: the parent folder (as Backspace and the toolbar's ↑)
                _vm.GoUpCommand.Execute(null);
                break;
            case Key.Down when !inTextBox && Keyboard.Modifiers == ModifierKeys.Alt && !IsMenuTakingKeys():
                if (e.IsRepeat)
                {
                    e.Handled = true; // a held Alt+↓ enters once (the old selection is still there while the folder loads)
                }
                else if (_vm.FolderToEnter() is { } folder)
                {
                    e.Handled = true;
                    _cascade.CloseAll(); // as Enter: OpenFolderTabAsync (a top-list folder) does not close it
                    _ = _vm.ActivateCardAsync(folder);
                }

                break;
        }
    }

    /// <summary>
    /// A menu takes the keys: the app's menu or Explorer's (shown or being prepared), or the focus in a
    /// ContextMenu (the toolbar's sort / filter menus). Alt+↑ ↓ then navigate nothing under it.
    /// </summary>
    private bool IsMenuTakingKeys() =>
        _cascade.IsMenuOpen
        || (Keyboard.FocusedElement is DependencyObject focused && RenameEditor.IsInContextMenu(focused));

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        NoteInput();
        _typedName.Clear(); // a click ends the typed name, as a key move does
        _vm.HideKeyboardCursor(); // the cursor's frame is for the keys only (a click moves the cursor itself)
        _cascade.WindowPressed(e.OriginalSource as DependencyObject); // a list the keys opened closes on a press outside it
        ReleaseButtonFocus(e.OriginalSource as DependencyObject);
        // A press anywhere else commits the name being edited (first: the branches below may handle
        // the press). The items have no focus to take, so a lost focus alone would not end it.
        _rename.CommitIfOutside(e.OriginalSource);
        if (IsAddressEditing && !RenameEditor.IsWithin(e.OriginalSource as DependencyObject, AddressEdit))
        {
            EndAddressEdit();
        }

        if (e.ChangedButton == MouseButton.Left && _sidebarJustToggled)
        {
            // ClickCount > 1: same spot and within the double-click time of the press that toggled
            // the sidebar. That press is dropped (a section header or a tab is there now); the first
            // press of a new click ends the guard.
            if (e.ClickCount > 1)
            {
                e.Handled = true;
                return;
            }

            _sidebarJustToggled = false;
        }

        // The mouse's back / forward buttons act on the pane under the pointer (handled here, at the
        // window's preview, before PaneView's own preview would make it active).
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2
            && PaneViewOf(e.OriginalSource as DependencyObject)?.Pane is { } pressed)
        {
            _vm.ActivatePane(pressed);
        }

        if (e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = true;
            _vm.GoBackCommand.Execute(null);
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            _vm.GoForwardCommand.Execute(null);
        }
    }

    /// <summary>
    /// The selection keys (not in a text box): the cursor's arrows, Home / End and Page keys (with
    /// Shift / Ctrl; → on a folder of the details list opens its cascade), Space / Ctrl+Space (a
    /// plain Space on a folder opens or closes its cascade instead), Ctrl+A, Esc (after the cascade had its turn), Shift+F10 / Apps
    /// (the selection's menu), Alt+Enter (プロパティ), Ctrl+C / Ctrl+X / Ctrl+V (and Ctrl+Insert /
    /// Shift+Insert), Delete (and Ctrl+D), F2 (rename), Enter (open) and Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z
    /// (the app's undo / redo of its own file operations; not while a menu takes the keys). Copy, cut, delete, rename and open act on the selection only, never on
    /// the item under the pointer (nor the row the cascade's keys highlight); a held key repeats
    /// nothing (one paste or one delete confirmation per press). Delete only recycles: Shift+Delete
    /// (Explorer's permanent delete) and Ctrl+Delete are not used. Returns true when the key was used.
    /// </summary>
    private bool HandleSelectionKey(Key key, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            _typedName.Clear();
            return _vm.ClearSelection();
        }

        // Arrows, Home / End, PageUp / PageDown move the keyboard cursor (ADR-1: the items never take
        // the focus; the cursor is the tab's SelectionModel.Cursor). Alt + arrows stay back / forward /
        // up / enter (the switch of OnPreviewKeyDown). The cascade had the plain arrows first.
        if (NavKeyOf(key) is { } nav && (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            if (IsMenuTakingKeys() || FocusedControlTakes(key))
            {
                return false;
            }

            // ADR-2: in the details list (one column, ← → move nothing sideways) a plain → on a folder
            // opens its list, like a tree's expand, and on a file its preview (← or Esc closes it).
            // The cards keep → for the move. A folder that has nothing to open moves on.
            if (nav == NavKey.Right && modifiers == ModifierKeys.None && ActiveList is DetailsListView
                && _vm.CursorCard() is { } item && (item.IsFolderLike || item.IsFile) && OpenCascadeFromKeys(item, toggle: false))
            {
                return true;
            }

            MoveCursorByKey(nav, modifiers);
            return true;
        }

        // Space: the cursor's item alone is selected; Ctrl+Space flips it. Not while a focused
        // button would be pressed by it (a toolbar button keeps the focus after a click).
        if (key == Key.Space && (modifiers & ~ModifierKeys.Control) == ModifierKeys.None)
        {
            if (IsMenuTakingKeys() || FocusedControlTakes(key))
            {
                return false;
            }

            if (modifiers == ModifierKeys.None && IsTypingName)
            {
                return false; // a space inside a typed name: its text input goes on to JumpToTypedName
            }

            if (e.IsRepeat)
            {
                return true; // a held Ctrl+Space flips once
            }

            // After Ctrl + arrows (the dotted cursor off the selection) a plain Space flips the
            // cursor's item into the selection too, a folder as well, instead of selecting it alone
            // or opening its list: Ctrl + arrows and Space pick items one by one, as Ctrl + click does.
            if (modifiers == ModifierKeys.None && IsCtrlMoveMode())
            {
                return _vm.SelectAtCursor(toggle: true);
            }

            // ADR-2: a plain Space on a folder opens (or, open, closes) its list instead of selecting
            // it; the selection stays as it is. Ctrl+Space always flips. A folder that has nothing to
            // open is selected, as a file is.
            if (modifiers == ModifierKeys.None && _vm.CursorCard() is { IsFolderLike: true } folder
                && OpenCascadeFromKeys(folder, toggle: true))
            {
                return true;
            }

            return _vm.SelectAtCursor(toggle: modifiers == ModifierKeys.Control);
        }

        // Shift+F10 / the Apps key: the selection's menu. Always used, also when it shows nothing:
        // an unhandled Shift+F10 (WM_SYSKEYDOWN) reaches DefWindowProc, which opens the window's
        // system menu (F10 enters its menu mode). The release is swallowed too (OnPreviewKeyUp).
        if ((key == Key.Apps && modifiers == ModifierKeys.None) || (key == Key.F10 && modifiers == ModifierKeys.Shift))
        {
            _menuKeyDown = key;
            if (!e.IsRepeat && _vm.HasTab && !IsMenuTakingKeys())
            {
                ShowMenuFromKeys();
            }

            return true;
        }

        // Alt+Enter: Explorer's プロパティ of the selection (nothing selected: of the folder shown).
        if (key == Key.Enter && modifiers == ModifierKeys.Alt)
        {
            if (IsMenuTakingKeys())
            {
                return false;
            }

            if (e.IsRepeat)
            {
                return true; // a held Alt+Enter opens one sheet
            }

            _cascade.CloseAll(); // as Enter: the sheet takes the foreground
            return _vm.ShowProperties();
        }

        if (key == Key.F2 && modifiers == ModifierKeys.None)
        {
            // The selected item (as Delete, never the item under the pointer); several: all of them
            // as "name (1)", "name (2)"…, the box on the focused one; nothing selected: nothing.
            if (!e.IsRepeat && _vm.RenameCandidate(out var batch) is { } card)
            {
                BeginRename(card, batch: batch);
            }

            return true;
        }

        // Ctrl+D is Explorer's other key for Delete: the same path (Recycle Bin, with the confirmation).
        if ((key == Key.Delete && modifiers == ModifierKeys.None) || (key == Key.D && modifiers == ModifierKeys.Control))
        {
            if (e.IsRepeat)
            {
                return true; // a held Delete asks once
            }

            var selected = _vm.SelectedItems();
            if (selected.Count == 0)
            {
                return false; // nothing selected: not used (never the item under the pointer)
            }

            e.Handled = true; // before the modal confirmation
            _cascade.CloseAll(); // as BeforeNavigate: the reload replaces the cascade
            _ = _vm.DeleteAsync(selected);
            return true;
        }

        if (key == Key.Enter && modifiers == ModifierKeys.None)
        {
            // A menu item takes Enter (the app's menu, Explorer's menu being prepared or shown).
            if (_cascade.IsMenuOpen
                || (Keyboard.FocusedElement is DependencyObject focused && RenameEditor.IsInContextMenu(focused)))
            {
                return false;
            }

            if (e.IsRepeat)
            {
                return true; // a held Enter opens once
            }

            if (_vm.SelectedItems().Count == 0)
            {
                return false; // nothing selected: not used (never the item under the pointer)
            }

            e.Handled = true; // before a modal confirmation (a program, many items)
            _cascade.CloseAll(); // as Delete: what opens replaces the cascade
            return _vm.OpenSelection();
        }

        // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z: the app's own history of its file operations (never
        // Explorer's, see RemovedVerbs in ShellContextMenu). Text boxes keep their own undo: this
        // method is not called while one has the focus.
        bool redoKeys = (key == Key.Y && modifiers == ModifierKeys.Control)
            || (key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        if ((key == Key.Z && modifiers == ModifierKeys.Control) || redoKeys)
        {
            if (IsMenuTakingKeys())
            {
                return false; // a menu has the keys
            }

            if (!e.IsRepeat) // a held key undoes once
            {
                _cascade.CloseAll(); // as Delete: the reload replaces the cascade
                _ = redoKeys ? _vm.RedoAsync() : _vm.UndoAsync();
            }

            return true;
        }

        // Ctrl+Insert / Shift+Insert: the old keys of コピー / 貼り付け, taken as Ctrl+C / Ctrl+V.
        if (key == Key.Insert && modifiers is (ModifierKeys.Control or ModifierKeys.Shift))
        {
            key = modifiers == ModifierKeys.Control ? Key.C : Key.V;
            modifiers = ModifierKeys.Control;
        }

        if (modifiers != ModifierKeys.Control || key is not (Key.A or Key.C or Key.X or Key.V))
        {
            return false;
        }

        if (e.IsRepeat)
        {
            return true; // swallowed: a held Ctrl+V must not start one paste per repeat
        }

        switch (key)
        {
            case Key.A:
                _cascade.CloseAll();
                _vm.SelectAll();
                break;
            case Key.C:
            case Key.X:
                var items = _vm.SelectedItems();
                if (items.Count > 0)
                {
                    _vm.CopyToClipboard(items, cut: key == Key.X);
                }

                break;
            case Key.V:
                if (_vm.PasteFolderOf(null) is { } folder)
                {
                    _ = _vm.PasteAsync(folder);
                }
                else
                {
                    _vm.Message.Info(_vm.HasTab && _vm.ActivePane.FolderTargetPath is not null && _vm.ActivePane.IsLoading
                        ? "フォルダの読み込みが終わってから貼り付けてください。"
                        : "この一覧には貼り付けできません。");
                }

                break;
        }

        return true;
    }

    private static NavKey? NavKeyOf(Key key) => key switch
    {
        Key.Left => NavKey.Left,
        Key.Right => NavKey.Right,
        Key.Up => NavKey.Up,
        Key.Down => NavKey.Down,
        Key.Home => NavKey.Home,
        Key.End => NavKey.End,
        Key.PageUp => NavKey.PageUp,
        Key.PageDown => NavKey.PageDown,
        _ => null,
    };

    /// <summary>
    /// The focused control uses <paramref name="key"/> itself, so the list's cursor leaves it alone:
    /// Space presses a focused button or ticks a box; a list, a slider or a scroll bar keeps its
    /// arrows. Text boxes never get here (HandleSelectionKey is not called in one).
    /// </summary>
    /// <summary>
    /// The button that still holds the keyboard focus although the list was used since (see
    /// <see cref="ReleaseButtonFocus"/>): Space is the list's, not that button's. Cleared by Tab (the
    /// user goes to the controls on purpose) and when another element takes the focus.
    /// </summary>
    private ButtonBase? _staleButton;

    /// <summary>
    /// A button keeps the keyboard focus after its click (and its frame is not drawn: the toolbar's
    /// and the sidebar's icon buttons have no focus visual), and the items never take the focus
    /// (ADR-1). Without this, after a click on e.g. the sidebar's 登録 button every Space went to it
    /// (<see cref="FocusedControlTakes"/>) and opened the folder dialog again, also after Ctrl +
    /// arrows. So a press on anything but that button (an item, the blank list, a sidebar row) and a
    /// move of the list's cursor by the keys mark the button stale. The focus itself is not moved:
    /// the window is a focus scope, and focusing it hands the focus straight back to the button
    /// (seen with Window.Focus), as does the activation after a dialog. A button reached with Tab
    /// still takes Space until the list is used. Text boxes are left alone.
    /// </summary>
    private void ReleaseButtonFocus(DependencyObject? pressed = null)
    {
        if (Keyboard.FocusedElement is ButtonBase button && (pressed is null || !RenameEditor.IsWithin(pressed, button)))
        {
            _staleButton = button;
        }
    }

    private bool FocusedControlTakes(Key key) => Keyboard.FocusedElement switch
    {
        ButtonBase button => key == Key.Space && !ReferenceEquals(button, _staleButton),
        Selector or Slider or ScrollBar => true,
        _ => false,
    };

    /// <summary>
    /// Space (any view) or → (the details list) on the cursor's folder: the list the hover would open
    /// (the card's overlay, the flyout beside the row's name), at once. The item is scrolled into view
    /// and laid out first (its row may not be realized: the card or the name cell is the element the
    /// list hangs on); that scroll closes whatever list was open. With <paramref name="toggle"/> a
    /// list already open on this folder closes instead. False when nothing opens (the key then acts
    /// as on a file). The selection is left alone: the cursor may be off it after Ctrl + arrows.
    /// </summary>
    private bool OpenCascadeFromKeys(CardViewModel folder, bool toggle)
    {
        // (a file row of the details list: its preview, see the → in HandleSelectionKey)
        _typedName.Clear(); // → in the details list: a move like the arrows (Space gets here only when no name is typed)
        if (_cascade.IsOpenOn(folder))
        {
            if (toggle)
            {
                _cascade.CloseAll();
            }

            return true;
        }

        return ActiveList.BringItemIntoView(folder) switch
        {
            CardView card => _cascade.OpenFromKeys(card),
            DetailsRowView row when CardAnchor.For(folder, _vm.Services.Icons) is { } anchor => anchor.File is not null
                ? _cascade.OpenPreviewFromKeys(row.CascadeAnchor, anchor)
                : _cascade.OpenFromKeys(row.CascadeAnchor, anchor),
            _ => false,
        };
    }

    /// <summary>
    /// The tab and the cursor item of the last Ctrl + arrow (Ctrl without Shift): while the cursor is
    /// still there, in that tab, with its frame drawn, a plain Space adds instead of replacing.
    /// </summary>
    private (PaneViewModel Tab, string Path)? _ctrlMovedTo;

    private bool IsCtrlMoveMode() =>
        _ctrlMovedTo is { } moved && ReferenceEquals(moved.Tab, _vm.ActivePane) && _vm.ActivePane.ShowsCursor
        && _vm.ActivePane.Selection.Cursor is { } cursor && PathTools.PathEquals(cursor, moved.Path);

    /// <summary>
    /// Moves the cursor of the shown list: a plain key selects the target alone, Shift the range from
    /// the anchor, Ctrl only moves the cursor (Ctrl+Shift adds the range). The target is scrolled
    /// into view through the virtualizing panel (its row may not be realized yet).
    /// </summary>
    private void MoveCursorByKey(NavKey nav, ModifierKeys modifiers)
    {
        ReleaseButtonFocus(); // the keys work the list now: the Space that follows must not press a button
        _typedName.Clear(); // as in Explorer, a move ends the typed name: a Space now selects, a letter starts anew
        _cascade.CloseAll(); // the cursor leaves the item a list or a preview may hang on
        _cascade.NoteKeyMove(); // the scroll below moves items under a resting pointer: not a hover
        var list = ActiveList;
        if (_vm.MoveCursor(nav, list.ItemsPerRow, list.PageRows,
                select: (modifiers & ModifierKeys.Control) == 0,
                extend: (modifiers & ModifierKeys.Shift) != 0) is { } card)
        {
            list.BringItemIntoView(card);
        }

        _ctrlMovedTo = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.Control
            && _vm.ActivePane.Selection.Cursor is { } at
            ? (_vm.ActivePane, at)
            : null;
    }

    /// <summary>
    /// A left press in the item area clears the selection, unless it is on an item (its click
    /// decides on release), on a scroll bar, a button or a grip, or a Ctrl / Shift click is meant.
    /// On the blank part of the list it also arms a marquee (rubber-band selection, as in Explorer):
    /// dragged past the system drag distance, the items the box touches are selected (Ctrl: flipped
    /// against the selection of the press, Shift: added to it). Not while the tab loads or a name
    /// is being edited.
    /// </summary>
    private void OnContentLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnItemOrControl(e.OriginalSource) || IsOnPaneBanner(e.OriginalSource))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
        if (modifiers == 0)
        {
            _vm.ClearSelection();
        }

        var list = ActiveList;
        // Any press (a quick second one too: nothing on the blank part reacts to a double click).
        if (e.RightButton == MouseButtonState.Pressed || !_vm.HasTab
            || _vm.ActivePane.IsLoading || IsNameBusy || list.IsMarqueeRunning
            || list.RealizedItemAt(e) is not null
            || _vm.BeginMarquee(modifiers) is not { } marquee)
        {
            return;
        }

        list.BeginMarquee(e, new MarqueeHandlers(
            Started: () => _cascade.BeginDrag(), // hidden and no hover while the box is dragged
            BoxChanged: box => _vm.UpdateMarquee(marquee, box),
            Ended: (cancelled, started) =>
            {
                if (cancelled)
                {
                    _vm.CancelMarquee(marquee);
                }

                if (started)
                {
                    _cascade.EndDrag();
                }
            }));
    }

    /// <summary>
    /// A right click on the blank area that nothing else took: the menu of the folder shown
    /// (貼り付け, 最新の情報に更新, its own entries and Explorer's background menu of it; only the first
    /// two without a folder on screen, see <see cref="ContextMenuFactory.ShowBlank"/>). A click
    /// that only reached the blank area because the items view does not hit test (while the tab
    /// loads) but lies on a realized item still gets that item's menu.
    /// </summary>
    private void OnContentRightUp(object sender, MouseButtonEventArgs e)
    {
        if (ActiveList.IsMarqueeRunning)
        {
            e.Handled = true; // the captured list sees the release: no menu, it would take the capture
            return;
        }

        if (e.Handled || !_vm.HasPanes || IsOnItemOrControl(e.OriginalSource) || IsOnPaneBanner(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        if (!_vm.HasTab)
        {
            // an empty pane (the press made it the active one): only the pane entries
            _cascade.CloseAll();
            _menus.ShowEmptyPane(ActivePaneView.ItemsHost);
            return;
        }

        if (ActiveList.RealizedItemAt(e) is { } view
            && ((view as CardView)?.ViewModel ?? (view as DetailsRowView)?.ViewModel) is { } item)
        {
            // No path in the log: the type of the source and the load state tell why it fell through.
            FolderDeck.Core.Infrastructure.AppLog.Info(
                $"Item right click reached the blank area: source {e.OriginalSource?.GetType().Name ?? "null"}, "
                + $"view {view.GetType().Name}, loading {_vm.ActivePane.IsLoading}");
            ItemContextMenu(item, view);
            return;
        }

        _cascade.CloseAll();
        _menus.ShowBlank(ActivePaneView.ItemsHost);
    }

    /// <summary>
    /// The element pressed is (inside) a card, a details row, a scroll bar, a button, a grip or a
    /// text box (the name's edit box: it sits in the list's adorner layer, outside the item view,
    /// and its own presses and context menu are its business).
    /// </summary>
    private static bool IsOnItemOrControl(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (node is CardView or DetailsRowView or ScrollBar or ButtonBase or Thumb or TextBoxBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The parent of an element; text runs are not visuals: they are walked up through the logical tree.</summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void OnHideMessage(object sender, RoutedEventArgs e) => _vm.Message.Hide();

    // The popups of the cascade are placed in screen coordinates: they close before the sidebar moves.
    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e) => _cascade.CloseAll();

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_vm.IsSidebarCollapsed)
        {
            _vm.SidebarWidth = SidebarColumn.ActualWidth; // never the 0 of a hidden sidebar
        }
    }

    /// <summary>
    /// Shows the sidebar at its saved width, or hides it together with the splitter. The column's
    /// minimum goes first: a width below the minimum is not applied, and a minimum left behind would
    /// keep an empty strip where the sidebar was.
    /// </summary>
    private void ApplySidebarLayout()
    {
        _cascade.CloseAll(); // a list beside a sidebar row loses its row, the others move
        bool collapsed = _vm.IsSidebarCollapsed;
        if (collapsed)
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            HideDropHint();
        }
        else
        {
            SidebarColumn.MinWidth = AppSettings.SidebarWidthMin;
            SidebarColumn.Width = new GridLength(AppSettings.ClampSidebarWidth(_vm.SidebarWidth));
        }

        SidebarPane.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// A view mode button became the checked one. After a click the command sets the same value
    /// again, and when the view model changed first this is its own value coming back: both do
    /// nothing. What is left is a button checked from outside (UI Automation's "select"), which
    /// runs no command.
    /// </summary>
    private void OnViewModeChecked(object sender, RoutedEventArgs e)
    {
        // DataContext, not _vm: the buttons exist before the constructor has set the field.
        if (sender is RadioButton { CommandParameter: CardShape shape } && DataContext is MainViewModel vm)
        {
            vm.CardShape = shape;
        }
    }

    /// <summary>
    /// Toolbar filter button (top level of 最近の履歴 / よく使う項目): everything, folders only or
    /// files only, the current one checked.
    /// </summary>
    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        _cascade.CloseAll();
        var current = _vm.ActivePane.Filter;
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = PlacementMode.Bottom,
        };
        foreach (var filter in ItemFilters.Values)
        {
            bool selected = filter == current;
            var item = new MenuItem
            {
                Header = FilterLabels.Long(filter),
                Tag = selected ? CheckGlyph : string.Empty, // the menu style draws Tag as a glyph
                IsCheckable = true,
                IsChecked = selected,
            };
            item.Click += (_, _) => _vm.SetFilter(filter);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Toolbar sort button: a menu of every mode, the current one checked, and on 最近の履歴 /
    /// よく使う項目 「手動の並びをリセット」 (enabled when cards were dragged there).
    /// </summary>
    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        _cascade.CloseAll();
        var current = _vm.ActivePane.SortMode;
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = PlacementMode.Bottom,
        };
        foreach (var mode in CardSortModes.All)
        {
            bool selected = mode == current;
            var item = new MenuItem
            {
                Header = SortModeLabels.Long(mode),
                Tag = selected ? "" : string.Empty, // check mark (the menu style draws Tag as a glyph)
                IsCheckable = true,
                IsChecked = selected,
            };
            item.Click += (_, _) => _vm.SetSortMode(mode);
            menu.Items.Add(item);
        }

        if (_vm.ShowResetManualOrder)
        {
            menu.Items.Add(new Separator());
            var reset = new MenuItem
            {
                Header = SortModeLabels.ResetManual,
                Tag = "", // undo glyph
                IsEnabled = _vm.CanResetManualOrder,
                ToolTip = SortModeLabels.ResetManualTip,
            };
            ToolTipService.SetShowOnDisabled(reset, true);
            reset.Click += (_, _) => _ = _vm.ResetManualOrderAsync();
            menu.Items.Add(reset);
        }

        menu.IsOpen = true;
    }

    // ---- drag & drop: registration and drops on folders (§10.1, P1) ----------------------------

    // Folders dropped from Explorer on the window are registered (the hint shows at the top of the
    // sidebar; no hint while the sidebar is hidden). Over a folder (a folder card or row, a
    // 登録フォルダ or drive row, a folder name of the address bar) a drop is a move or copy into it
    // instead, also for the app's own drags (FolderDropOf); so is the rest of the list area of a
    // tab that shows a folder (the shown folder, FolderTargetAt). Refused there, it is no registration
    // either, and nowhere in the address bar is (IsInAddressBar). Drags the app started itself
    // (cards, rows, previews, sidebar rows) carry a file drop too, for other apps: released elsewhere
    // over the window they are never a registration.
    private static bool CanDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && !IsOwnDrag(e.Data);

    private static bool IsOwnDrag(IDataObject data) => FileDragOut.IsDragging || FileDrag.IsFromThisApp(data.GetDataPresent);

    /// <summary>
    /// The folder under the pointer during a drag, and what to highlight (a <see cref="CardViewModel"/>,
    /// <see cref="SidebarItemViewModel"/> or <see cref="BreadcrumbItem"/>; null for the list itself).
    /// <paramref name="ShownFolder"/>: the blank part of the shown folder's list or one of its files,
    /// which drops into that folder (Explorer's rule; Ctrl may duplicate in place there).
    /// <paramref name="OnFile"/>: the path of the file card or row under the pointer (a release on a
    /// dragged item itself does nothing, as in Explorer). <paramref name="Refused"/>: a part of a real
    /// folder's list that takes no drop now (loading, a failed load, a busy or missing folder item);
    /// it refuses the drop instead of making it a registration, so the list has no registering holes.
    /// </summary>
    private readonly record struct FolderTarget(string Folder, object? Mark, bool ShownFolder = false, string? OnFile = null, bool Refused = false);

    /// <summary>
    /// A drag over a folder: the folder, the dragged paths, the effect a release would have there,
    /// and whether that release duplicates items already in the shown folder (<see cref="FileDrag.CopiesInPlace"/>).
    /// </summary>
    private readonly record struct FolderDrop(FolderTarget Target, IReadOnlyList<string> Sources, DropEffects Effect, bool CopyInPlace);

    /// <summary>The folder highlighted as the drop target (null: none).</summary>
    private object? _dropMark;

    /// <summary>The file drop of another app's drag, read once per drag (DragOver comes many times a second).</summary>
    private (IDataObject Data, string[]? Paths)? _externalDrop;

    /// <summary>
    /// A drag from another app comes in: the cascade closes (its popups would cover the folders and
    /// are not drop targets). The app's own drags have hidden it already (<see cref="CascadeController.BeginDrag"/>).
    /// </summary>
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!FileDragOut.IsDragging)
        {
            _cascade.CloseAll();
        }

        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        NoteInput(); // OLE calls it over and over during a drag over the window
        if (DragOverEmptyPane(e))
        {
            return; // a folder onto an empty pane: it opens there
        }

        if (FolderDropOf(e) is { } drop)
        {
            e.Effects = ToWpf(drop.Effect);
            SetDropMark(drop.Effect == DropEffects.None ? null : drop.Target.Mark);
            string name = PathTools.DisplayName(drop.Target.Folder);
            ShowDropHint(drop.Effect switch
            {
                DropEffects.Move => $"「{name}」へ移動",
                DropEffects.Copy => $"「{name}」へコピー",
                DropEffects.Link => $"「{name}」にショートカットを作成",
                _ => null,
            });
            e.Handled = true;
            return;
        }

        SetDropMark(null);
        bool ok = CanDrop(e) && !IsInAddressBar(e.OriginalSource);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropHint(ok ? "ドロップして登録" : null);
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DropOnEmptyPane(e))
        {
            SetDropMark(null);
            return;
        }

        var drop = FolderDropOf(e); // the position and the keys at the release decide again
        SetDropMark(null);
        HideDropHint();
        _externalDrop = null;
        if (drop is { } d)
        {
            e.Handled = true;
            if (d.Target.Mark is BreadcrumbItem)
            {
                _crumbQuiet = true; // the pointer rests on the crumb after the drop: no list under it until it leaves (OnCrumbMouseLeave)
            }

            if (d.Effect is not (DropEffects.Move or DropEffects.Copy or DropEffects.Link))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            if (d.Effect == DropEffects.Link)
            {
                // Alt: shortcuts, made by the app after the drop (the source keeps its items).
                e.Effects = DragDropEffects.Link;
                _altDropUp = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
                if (FileDragOut.Current is { } linker)
                {
                    linker.Dropped = new InAppDrop(d.Target.Folder, TransferKind.Copy, Link: true);
                    return;
                }

                string[] linked = [.. d.Sources];
                string linkFolder = d.Target.Folder;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = _vm.LinkDropAsync(linked, linkFolder, fromOtherApp: true));
                return;
            }

            var kind = d.Effect == DropEffects.Move ? TransferKind.Move : TransferKind.Copy;

            // Copy, never Move: the app moves the items itself, after the drop. Move would let the
            // source delete the originals, also those then skipped in the shell's dialogs.
            e.Effects = ToWpf(FileDrag.DropResult(d.Effect));
            if (FileDragOut.Current is { } own)
            {
                own.Dropped = new InAppDrop(d.Target.Folder, kind, d.CopyInPlace); // run by FinishDrag once DoDragDrop returned
                return;
            }

            // Another app's drag: let its DoDragDrop return first (Explorer waits for this Drop).
            string[] paths = [.. d.Sources];
            string folder = d.Target.Folder;
            bool copyInPlace = d.CopyInPlace;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = _vm.DropAsync(paths, folder, kind, fromOtherApp: true, copyInPlace));
            return;
        }

        if (!CanDrop(e) || IsInAddressBar(e.OriginalSource) || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped)
        {
            return;
        }

        e.Handled = true;
        await _vm.RegisterDroppedAsync(dropped);
    }

    /// <summary>
    /// The element under the pointer is in the address bar. Its folder names take drops
    /// (<see cref="FolderTargetAt"/>); the rest of it (separators, 「…」, the room after the crumbs,
    /// the edit box) is no registration either, or the hint would flicker while a drag crosses the crumbs.
    /// </summary>
    private bool IsInAddressBar(object? originalSource)
    {
        for (var node = originalSource as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, AddressBar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The folder a drag is over and the effect a release would have, or null when it is not over a
    /// folder (then the drop is a registration outside the address bar, or nothing for the app's own drags). A 登録フォルダ
    /// row dragged for reordering never drops into a folder. Strings only: DragOver runs it often.
    /// </summary>
    private FolderDrop? FolderDropOf(DragEventArgs e)
    {
        var own = FileDragOut.Current;
        if (own is { IntoFolders: false } || FolderTargetAt(e.OriginalSource) is not { } target)
        {
            return null;
        }

        IReadOnlyList<string>? sources = own is not null ? own.Paths : ExternalPaths(e.Data);
        if (sources is not { Count: > 0 })
        {
            return null;
        }

        // the last crumb is the shown folder: the items are there already (no import into it either);
        // the shown folder's list takes them, and duplicates what is there with Ctrl
        var keys = KeysOf(e.KeyStates);
        var allowed = FromWpf(e.AllowedEffects);
        var effect = target.Refused || _vm.IsFileOperationRunning || IsNameBusy || target.Mark is BreadcrumbItem { IsLast: true }
                || (target.OnFile is { } onFile && sources.Any(s => PathTools.PathEquals(s, onFile)))
            ? DropEffects.None
            : target.ShownFolder
                ? FileDrag.DropEffectForShownFolder(sources, target.Folder, keys, allowed)
                : FileDrag.DropEffectFor(sources, target.Folder, keys, allowed);
        bool copyInPlace = target.ShownFolder && effect == DropEffects.Copy && FileDrag.CopiesInPlace(keys);
        return new FolderDrop(target, sources, effect, copyInPlace);
    }

    /// <summary>
    /// The usable folder item the element under the pointer belongs to: a folder card or details
    /// row (not busy), a 登録フォルダ or drive row, a folder name of the address bar (its path, not
    /// checked here: the transfer does). Anywhere else in the list area (<see cref="ContentHost"/>:
    /// between and after the items, a file card or row, the column titles, the scroll bars) it is
    /// the shown folder, when the tab shows one that is on screen (<see cref="MainViewModel.PasteFolderOf"/>
    /// with no item: not the top lists, not while loading or after a failed load), with no mark.
    /// In a real folder's list that takes no drop now (loading, a failed load, an unusable folder
    /// item: busy, missing) it is a refused target, never null, so an Explorer drag there is no
    /// registration. Null for those on the top lists, headers, クイック アクセス, the crumbs'
    /// separators and everything else. While a tab loads its items do not hit test, so they are not
    /// found (the blank area is refused then).
    /// </summary>
    private FolderTarget? FolderTargetAt(object? originalSource)
    {
        var pane = PaneViewOf(originalSource as DependencyObject)?.Pane; // a drop on a pane that is not the active one is that pane's
        for (var node = originalSource as DependencyObject; node is not null; node = ParentOf(node))
        {
            switch (node)
            {
                case CardView { ViewModel: { IsFile: true } file }:
                    return ShownFolderTarget(file.Path); // Explorer: a drop on a file goes into its folder
                case DetailsRowView { ViewModel: { IsFile: true } file }:
                    return ShownFolderTarget(file.Path);
                case CardView { ViewModel: { } card }:
                    return FolderCard(card);
                case DetailsRowView { ViewModel: { } row }:
                    return FolderCard(row);
                case PaneView view when pane is not null && IsInItemsHost(view, originalSource as DependencyObject):
                    return ShownFolderTarget();
                case GridSplitter splitter when _splitOf.ContainsKey(splitter):
                    return RefusedInFolder(); // between two panes: no registration, no transfer
                case Button { DataContext: BreadcrumbItem crumb }:
                    // a folder of the address bar (not a fixed tab's list or （不明）); the last one is the shown folder (FolderDropOf)
                    return crumb.ToolTip is { } path && PathTools.IsFullyQualified(path) ? new FolderTarget(path, crumb) : null;
                case FrameworkElement { DataContext: SidebarItemViewModel item }:
                    return _vm.PasteFolderOf(item.ToItemRef()) is { } folder ? new FolderTarget(folder, item) : null;
            }
        }

        return null;

        FolderTarget? FolderCard(CardViewModel card) =>
            !card.IsBusy && _vm.PasteFolderOf(card.ToItemRef()) is { } folder ? new FolderTarget(folder, card) : RefusedInFolder();

        // no highlight: the whole list is the target, and a card's mark would say "into this folder"
        FolderTarget? ShownFolderTarget(string? onFile = null) =>
            HasTabIn(pane) && _vm.PasteFolderOf(null, pane) is { } shown ? new FolderTarget(shown, null, ShownFolder: true, onFile) : RefusedInFolder();

        // a real folder's list that takes no drop now refuses it; only the top lists (最近の履歴 /
        // よく使う項目 / PC) and the placeholder keep the old null (a registration for Explorer drags)
        FolderTarget? RefusedInFolder() =>
            HasTabIn(pane) && (pane ?? _vm.ActivePane).FolderTargetPath is { } real ? new FolderTarget(real, null, Refused: true) : null;

        // the pane of the drop shows a tab (not an empty pane, not the placeholder); the active one off the panes
        bool HasTabIn(PaneViewModel? target) => !(target ?? _vm.ActivePane).IsPlaceholder;
    }

    /// <summary>The paths of another app's file drop (null when it carries none or cannot be read).</summary>
    private string[]? ExternalPaths(IDataObject data)
    {
        if (_externalDrop is { } known && ReferenceEquals(known.Data, data))
        {
            return known.Paths;
        }

        string[]? paths;
        try
        {
            paths = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            paths = null;
        }

        _externalDrop = (data, paths);
        return paths;
    }

    /// <summary>Highlights the folder a release would drop into (IsMouseOver does not follow a drag).</summary>
    private void SetDropMark(object? mark)
    {
        if (ReferenceEquals(mark, _dropMark))
        {
            return;
        }

        Mark(_dropMark, false);
        _dropMark = mark;
        Mark(mark, true);

        static void Mark(object? target, bool on)
        {
            switch (target)
            {
                case CardViewModel card:
                    card.IsDropTarget = on;
                    break;
                case SidebarItemViewModel item:
                    item.IsDropTarget = on;
                    break;
                case BreadcrumbItem crumb:
                    crumb.IsDropTarget = on;
                    break;
            }
        }
    }

    /// <summary>Shows what a release does at the top of the sidebar (null hides it; never while the sidebar is hidden).</summary>
    private void ShowDropHint(string? text)
    {
        if (text is null || _vm.IsSidebarCollapsed)
        {
            DropHint.Visibility = Visibility.Collapsed;
            return;
        }

        DropHintText.Text = text;
        DropHint.Visibility = Visibility.Visible;
    }

    private void HideDropHint() => ShowDropHint(null);

    private static DropKeys KeysOf(DragDropKeyStates keys)
    {
        var result = DropKeys.None;
        if ((keys & DragDropKeyStates.ShiftKey) != 0)
        {
            result |= DropKeys.Shift;
        }

        if ((keys & DragDropKeyStates.ControlKey) != 0)
        {
            result |= DropKeys.Control;
        }

        if ((keys & DragDropKeyStates.AltKey) != 0)
        {
            result |= DropKeys.Alt;
        }

        if ((keys & DragDropKeyStates.RightMouseButton) != 0)
        {
            result |= DropKeys.RightButton;
        }

        return result;
    }

    private static DragDropEffects ToWpf(DropEffects effects) => (DragDropEffects)(int)effects;

    private static DropEffects FromWpf(DragDropEffects effects) =>
        (DropEffects)(int)(effects & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link));

    // ---- ICardHost ----------------------------------------------------------------------------

    /// <summary>
    /// The view that shows the items of the tab right now, by the view mode: the card grid or the
    /// details list. Scroll position and reorder drag go through it. The other one is hidden and
    /// has no items (MainWindow.xaml), so its scroll position is never read or set.
    /// </summary>
    private IItemsView ActiveList => ActivePaneView.ActiveList;

    public void CardMouseEnter(CardView card) => _cascade.CardMouseEnter(card);

    public void CardMouseLeave(CardView card) => _cascade.CardMouseLeave(card);

    /// <summary>
    /// A row of the details list: the name cell (icon + name + 📌) is both what the pointer rests on
    /// and what the list is placed beside (right after the name), so resting on the blank after the
    /// name or on the other columns opens nothing. A folder opens its list, a previewable file its preview; on any other row nothing is
    /// armed and a list that is open closes as on leaving its row.
    /// </summary>
    public void RowMouseEnter(FrameworkElement nameColumn, CardViewModel vm)
    {
        if (CardAnchor.For(vm, _vm.Services.Icons) is { } anchor)
        {
            _cascade.AnchorMouseEnter(nameColumn, nameColumn, anchor);
        }
    }

    /// <summary>The pointer moved on a name column without a button held: arms the row again after everything was closed under it.</summary>
    public void RowMouseMove(FrameworkElement nameColumn, CardViewModel vm)
    {
        if (CardAnchor.For(vm, _vm.Services.Icons) is { } anchor)
        {
            _cascade.AnchorMouseMove(nameColumn, nameColumn, anchor, Mouse.GetPosition(this));
        }
    }

    public void RowMouseLeave(FrameworkElement nameColumn) => _cascade.AnchorMouseLeave(nameColumn);

    /// <summary>
    /// A click with Ctrl and / or Shift selects (Ctrl flips the item, Shift takes the range, both add
    /// the range); a plain click clears the selection and opens the item. With the setting
    /// ダブルクリックで開く a plain click selects the item alone and a double click opens it (the
    /// item stays selected, as in Explorer, and a program runs without 実行形式の確認).
    /// </summary>
    public void ItemClick(CardViewModel vm, FrameworkElement element, ModifierKeys modifiers, bool doubleClick)
    {
        _cascade.CloseAll();
        if (_vm.ClickSelect(vm, modifiers))
        {
            return;
        }

        if (_vm.OpensOnDoubleClick)
        {
            if (doubleClick)
            {
                _ = _vm.ActivateCardAsync(vm, askBeforeProgram: false); // a double click is deliberate: no 実行形式の確認
            }
            else
            {
                _vm.SelectOnly(vm);
            }

            return;
        }

        _vm.ClearSelection();
        _ = _vm.ActivateCardAsync(vm);
    }

    public bool OpensOnDoubleClick => _vm.OpensOnDoubleClick;

    public bool CascadeOnNameColumn => _vm.CascadeOnNameColumn;

    // ---- the name's edit box (F2, 名前の変更, 新しいフォルダー) -------------------------------------

    /// <summary>
    /// Opens the edit box over <paramref name="card"/>'s name (scrolled into view first), with
    /// <paramref name="text"/> or its name. Nothing opens for a card that cannot be renamed now
    /// (said on the message line) or that the shown view does not have. With a <paramref name="batch"/>
    /// of several paths (F2 on a selection) the committed name renames all of them.
    /// </summary>
    private void BeginRename(CardViewModel card, string? text = null, string? typeAhead = null, IReadOnlyList<string>? batch = null)
    {
        // The card shown now for the same item: a card held since a menu opened, or from before a
        // reload, has been replaced by an equal one.
        card = _vm.ShownCard(card.Path) ?? card;
        if (_rename.IsOpen || !_vm.CanStartRename(card))
        {
            return;
        }

        _cascade.CloseAll();

        // The scroll happens inside this call (Scrolled closes what hangs on the items): the box comes after it.
        if (ActiveList.BringItemIntoView(card) is not { } view)
        {
            return;
        }

        if (_rename.Begin(view, card, text ?? card.Name, typeAhead))
        {
            _renameBatch = batch is { Count: > 1 } ? batch : null;
            _cascade.SetEditing(true);
            if (_renameBatch is { } many)
            {
                _vm.Message.Info($"選択した {many.Count} 個の項目の名前を「名前 (1)」「名前 (2)」…に変更します。");
            }
        }
    }

    /// <summary>
    /// The edit box closed with a commit: renames, and opens the box again when the name has to
    /// change (taken, extension declined) and the item is still shown (if not, the message of the
    /// rename stays and the typed name is dropped). A <paramref name="batch"/> is renamed together
    /// and never opens the box again.
    /// </summary>
    private async Task CommitRenameAsync(CardViewModel card, string text, IReadOnlyList<string>? batch = null)
    {
        try
        {
            if (batch is { Count: > 1 })
            {
                await _vm.RenameManyAsync(card, text, batch);
            }
            else if (await _vm.RenameAsync(card, text) && _vm.ShownCard(card.Path) is not null)
            {
                BeginRename(card, text);
            }
        }
        finally
        {
            _committingRename = false;
            RunPendingActivation();
        }
    }

    /// <summary>Ctrl+Shift+N and 新しいフォルダー: creates the folder, then opens its edit box with what was typed meanwhile.</summary>
    private async Task CreateFolderAndRenameAsync()
    {
        if (_creatingFolder)
        {
            return;
        }

        _cascade.CloseAll();

        // The keys typed meanwhile must reach no control: a toolbar button keeps the focus after a
        // click (Space would press it, arrows would switch the view) and would eat the text input.
        RenameEditor.FocusWindowItself(this);
        _creatingFolder = true;
        _typeAhead.Clear();
        try
        {
            if (await _vm.CreateFolderAsync() is not { } card)
            {
                return;
            }

            // The reload puts the scroll position back at Loaded priority: bring the folder into view after that.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            string typed = _typeAhead.ToString();
            _creatingFolder = false;
            BeginRename(card, typeAhead: typed);
        }
        finally
        {
            _creatingFolder = false;
            _typeAhead.Clear();
            RunPendingActivation();
        }
    }

    /// <summary>
    /// While Ctrl+Shift+N makes its folder, typed text waits for the folder's edit box. Otherwise
    /// letters typed on the list jump to a name (<see cref="JumpToTypedName"/>).
    /// </summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_creatingFolder && !_rename.IsOpen)
        {
            _typeAhead.Append(e.Text);
            e.Handled = true;
            return;
        }

        if (JumpToTypedName(e.Text))
        {
            e.Handled = true;
        }
    }

    /// <summary>The letters of the jump to a typed name, joined while each comes within <see cref="TypedNamePauseMs"/> of the last.</summary>
    private readonly System.Text.StringBuilder _typedName = new();
    private long _typedNameAt;
    private const int TypedNamePauseMs = 1000;

    /// <summary>A typed name is still being joined: a Space now is part of it, not the selection key.</summary>
    private bool IsTypingName => _typedName.Length > 0 && Environment.TickCount64 - _typedNameAt <= TypedNamePauseMs;

    /// <summary>
    /// Letters typed on the list move the cursor to the next item whose name starts with them, as in
    /// Explorer, and select it alone; letters within a second of each other are joined into one
    /// name. Not in a text box or a list with its own search, not while a name is edited, a marquee
    /// runs or a menu has the keys, nor for Ctrl / Alt chords. Only committed text reaches
    /// TextInput (an IME's composition raises TextInputUpdate, and its keys arrive as ImeProcessed),
    /// so a converted name is matched as a whole. Returns true when the text was used.
    /// </summary>
    private bool JumpToTypedName(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl) // Enter, Backspace, Esc, Ctrl + letter
            || !_vm.HasTab || IsNameBusy || ActiveList.IsMarqueeRunning || IsMenuTakingKeys()
            || Keyboard.FocusedElement is TextBoxBase or Selector
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return false;
        }

        if (!IsTypingName)
        {
            _typedName.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false; // no name starts with a space: Space alone is not a jump
            }
        }

        _typedName.Append(text);
        _typedNameAt = Environment.TickCount64;
        _cascade.CloseAll(); // as the arrows: the cursor leaves the item a list may hang on
        _cascade.NoteKeyMove();
        if (_vm.MoveCursorToName(_typedName.ToString()) is { } card)
        {
            ActiveList.BringItemIntoView(card);
        }

        return true;
    }

    /// <summary>A name is edited, or a folder made for its edit box, or a committed name handled: an activation's reload waits.</summary>
    private bool IsNameBusy => _rename.IsOpen || _creatingFolder || _committingRename;

    bool IMainView.IsNameBusy => IsNameBusy;

    /// <summary>The activation's checks held back while a name was edited (<see cref="_activationPending"/>) run now, if nothing is edited any more.</summary>
    private void RunPendingActivation()
    {
        if (_activationPending && !IsNameBusy)
        {
            _activationPending = false;
            _ = _vm.OnActivatedAsync();
        }
    }

    /// <summary>
    /// The menu of an item that is part of a selection of several applies to the whole selection;
    /// a right click outside the selection clears it and shows the item's own menu.
    /// </summary>
    public void ItemContextMenu(CardViewModel vm, FrameworkElement element) => ShowItemMenu(vm, element, keyPoint: null);

    /// <summary>
    /// <see cref="ItemContextMenu"/>; with <paramref name="keyPoint"/> (Shift+F10, the Apps key) the
    /// menu opens at that point of <paramref name="element"/> rather than at the pointer.
    /// </summary>
    private void ShowItemMenu(CardViewModel vm, FrameworkElement element, Point? keyPoint)
    {
        var item = vm.ToItemRef();
        var targets = _vm.SelectionTargets(item);
        if (targets.Count == 1 && !_vm.ActivePane.Selection.Contains(vm.Path))
        {
            _vm.ClearSelection();
        }

        _menus.Show(item, element, targets, keyPoint);
    }

    /// <summary>
    /// Shift+F10 / the Apps key: the menu a right click on the selection shows, at the bottom-left
    /// corner of the cursor's item (or of the first selected one when the cursor is not on the
    /// selection), scrolled into view first; nothing selected: the menu of the blank area (the
    /// folder shown) at the list's top-left corner. Whether it is the app's menu or Explorer's
    /// follows the setting, as for the mouse (the same <see cref="ContextMenuFactory"/> calls).
    /// </summary>
    private void ShowMenuFromKeys()
    {
        _cascade.CloseAll(); // the menu is the list's, not the row a list's keys highlight
        var tab = _vm.ActivePane;
        var selected = tab.SelectedCards();
        if (selected.Count > 0)
        {
            var card = _vm.CursorCard() is { } cursor && tab.Selection.Contains(cursor.Path) ? cursor : selected[0];
            if (ActiveList.BringItemIntoView(card) is { } element)
            {
                ShowItemMenu(card, element, new Point(0, element.ActualHeight));
            }
            else
            {
                ShowItemMenu(card, ActivePaneView.ItemsHost, new Point(0, 0)); // not laid out yet: still the selection's menu
            }

            return;
        }

        _menus.ShowBlank(ActivePaneView.ItemsHost, new Point(0, 0));
    }

    public void ItemRemove(CardViewModel vm) => _vm.RemoveCard(vm);

    public void ItemUnpin(CardViewModel vm)
    {
        if (!vm.IsDisabled && _vm.IsPinned(vm.Path))
        {
            _cascade.CloseAll(); // the card may move to its sorted place
            _vm.TogglePin(vm.ToItemRef());
        }
    }

    public void ItemShowInExplorer(CardViewModel vm)
    {
        if (!vm.IsDisabled)
        {
            _cascade.CloseAll();
            _ = _vm.ShowInExplorerAsync(vm.ToItemRef());
        }
    }

    // ---- sidebar rows (登録フォルダ, drives) ---------------------------------------------------

    private void OnSidebarClicked(SidebarItemViewModel item)
    {
        _cascade.CloseAll();
        _ = item.IsDrive ? _vm.OpenDriveAsync(item) : _vm.OpenRegisteredAsync(item);
    }

    /// <summary>Right click on the PC header: 「エクスプローラーで開く」 (This PC). The cascade of a row is closed first (the menu is not about it).</summary>
    private void OnPcHeaderContextMenu(FrameworkElement header)
    {
        _cascade.CloseAll();
        _menus.ShowPcSection(header);
    }

    private void OnSidebarExplorer(SidebarItemViewModel item)
    {
        // A drive that is not connected is still handed to Explorer, which offers to reconnect it (as in its menu).
        if (!item.IsDisabled || item.IsDrive)
        {
            _cascade.CloseAll();
            _ = _vm.ShowInExplorerAsync(item.ToItemRef());
        }
    }

    /// <summary>
    /// A 登録フォルダ row is dragged (a drive row never is): released among the 登録フォルダ rows it
    /// moves the row (a section header, a quick row or a drive row refuses it), released over
    /// another app that app gets the folder (a missing folder can only be moved).
    /// </summary>
    private void OnSidebarDrag(SidebarItemViewModel item)
    {
        if (item.IsDrive)
        {
            return;
        }

        DataObject? data = item.IsDisabled ? null : FileDragOut.CreateData([item.Path], reorder: false);
        if (data is null)
        {
            data = new DataObject();
            data.SetData(FileDrag.SourceFormat, FileDrag.SourceFormat);
        }

        data.SetData(FileDrag.SidebarReorderFormat, FileDrag.SidebarReorderFormat);

        // Only reorders inside the app (never dropped into a folder), and never offers Move.
        var drag = new OutgoingDrag(data, item.IsDisabled ? [] : [item.Path], moveAllowed: false, intoFolders: false);
        int? index;
        _cascade.BeginDrag();
        try
        {
            index = Sidebar.RunReorderDrag(item, source => FileDragOut.Run(source, drag, "sidebar row"));
        }
        finally
        {
            _cascade.EndDrag();
            SetDropMark(null);
            HideDropHint();
        }

        if (index is { } i)
        {
            _vm.MoveRegistered(item, i);
        }
    }

    public bool CanDragItem(CardViewModel vm) => !vm.IsDisabled && !vm.IsBusy && FileDragOut.CanDrag(vm.Path);

    /// <summary>
    /// A drag of an item that is part of the selection carries the whole selection (in display
    /// order), else the item alone. At the top level of a tab a drag of one card also carries the
    /// reorder format: released over the list it moves the card. Released over a folder of the app
    /// (a folder card or row, a 登録フォルダ or drive row) the items are moved or copied into it once
    /// the drag has ended (<see cref="MainViewModel.FinishDrag"/>); released over another app, that
    /// app gets the files. Move is offered only when every item can be cut (items of a folder).
    /// </summary>
    public void ItemDrag(CardViewModel vm)
    {
        if (!CanDragItem(vm))
        {
            return;
        }

        var targets = _vm.SelectionTargets(vm.ToItemRef());
        var usable = targets
            .Where(t => !t.IsDisabled && t.Card is not ({ IsDisabled: true } or { IsBusy: true }) && FileDragOut.CanDrag(t.Path))
            .ToList();
        var paths = usable.Select(t => t.Path).ToList();
        bool reorder = targets.Count == 1 && _vm.CanReorder(vm);
        bool move = usable.Count > 0 && !_vm.IsFileOperationRunning && usable.All(MainViewModel.CanCut);
        if (FileDragOut.CreateData(paths, reorder) is not { } data)
        {
            return;
        }

        var drag = new OutgoingDrag(data, paths, move, intoFolders: true);

        var list = ActiveList; // the view the drag started in, also once it has ended
        int? index = null;
        _cascade.BeginDrag();
        try
        {
            if (reorder)
            {
                index = list.RunReorderDrag(vm, source => FileDragOut.Run(source, drag, "top card"));
            }
            else
            {
                FileDragOut.Run(this, drag, targets.Count > 1 ? "selection" : "card"); // the window outlives any item container
            }
        }
        finally
        {
            _cascade.EndDrag();
            SetDropMark(null);
            HideDropHint();
        }

        // A drop on the list itself reorders; the grid handles it, so the window records no folder drop.
        if (index is { } i)
        {
            double offset = list.VerticalOffset;
            _vm.MoveCard(vm, i);
            list.ScrollTo(offset); // the list rebuilds its rows: stay where the item was dropped
        }

        _vm.FinishDrag(drag);
    }

    // ---- drives of the PC section --------------------------------------------------------------

    /// <summary>How often, at most, an activation of the window reads the drives again.</summary>
    private const long DrivesActivationIntervalMs = 5000;

    /// <summary>
    /// A volume came or went (USB drive, card, disc): the list is read again and every drive is
    /// asked again (a card reader keeps its letter while the card changes). The clipboard changed:
    /// a pending cut of this app ends when the clipboard holds something else.
    /// </summary>
    private void WatchWindowMessages()
    {
        _hook = new MainWindowHook(this);
        _hook.DrivesChanged += () => _ = _vm.DriveList.RefreshAsync(recheck: true);
        _hook.ClipboardChanged += _vm.OnClipboardChanged;
        _hook.DisplayChanged += OnDisplayChanged;
        _hook.Activated += OnHookActivated;
    }

    /// <summary>
    /// Windows sends no message when a network drive is mapped, unmapped or reconnected: coming back
    /// to the window is when the list is brought up to date, at most once in 5 s. Only new drives
    /// and drives that were not usable are asked again (F5 asks all of them).
    /// </summary>
    private void OnActivatedReadDrives(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (now - _drivesReadAt < DrivesActivationIntervalMs)
        {
            return;
        }

        _drivesReadAt = now;
        _ = _vm.DriveList.RefreshAsync(recheck: false);
    }

    // ---- display changes (a monitor plugged in or out, Win + P, resolution, work area) ------------

    /// <summary>How long after a display change an activation also puts the window on top of the z-order.</summary>
    private const long RaiseAfterDisplayChangeMs = 120_000;

    /// <summary>When the last display change was reported (null: none yet).</summary>
    private long? _displayChangedAt;

    /// <summary>The activation after a display change was logged (once per change).</summary>
    private bool _raiseLogged;

    /// <summary>
    /// The monitors changed while the app runs (HDMI unplugged or moved to another monitor, a
    /// projection mode, a resolution, the taskbar). The cause of "the window no longer comes to
    /// the front" afterwards is not known: one display: line records what is needed to tell, then
    /// the cascade (topmost popups placed for the old layout) is closed, windows whose title bar is
    /// on no monitor are brought back, and WPF is made to build its render target again.
    /// </summary>
    private void OnDisplayChanged()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _displayChangedAt = Environment.TickCount64;
        _raiseLogged = false;
        AppLog.Info($"display: changed; state {WindowState}, active {IsActive}, enabled {IsEnabled}, render tier {System.Windows.Media.RenderCapability.Tier >> 16}, "
            + $"{_cascade.DiagnosticState}; main {ShellInterop.DescribeWindow(hwnd, DpiScaleOf(this))}");
        foreach (Window owned in OwnedWindows)
        {
            var ownedHwnd = new WindowInteropHelper(owned).Handle;
            if (ownedHwnd != IntPtr.Zero)
            {
                AppLog.Info($"display: owned {owned.GetType().Name}, state {owned.WindowState}, visible {owned.IsVisible}; {ShellInterop.DescribeWindow(ownedHwnd, DpiScaleOf(owned))}");
            }
        }

        _cascade.CloseAll(); // skipped while Explorer's menu or a drag is on (they close it when they end)
        EnsureOnScreen(this, hwnd, new Point(0, 0)); // the primary monitor holds (0, 0)
        var main = ShellInterop.GetWindowRect(hwnd);
        var ownerCenter = main is { } m ? new Point(m.Left + (m.Width / 2), m.Top + (m.Height / 2)) : new Point(0, 0);
        foreach (Window owned in OwnedWindows)
        {
            var ownedHwnd = new WindowInteropHelper(owned).Handle;
            if (ownedHwnd != IntPtr.Zero && owned.IsVisible)
            {
                EnsureOnScreen(owned, ownedHwnd, ownerCenter);
            }
        }

        RefreshRendering(hwnd);
    }

    private static double DpiScaleOf(System.Windows.Media.Visual visual) => Math.Max(1.0, System.Windows.Media.VisualTreeHelper.GetDpi(visual).DpiScaleX);

    /// <summary>
    /// A normal window whose title bar strip is on no monitor (RestorePlacement's rule) is centred
    /// in the work area of the monitor nearest to <paramref name="target"/>. Everything in physical
    /// pixels: Left / Top are DIPs of the window's own monitor and SystemParameters.WorkArea those of
    /// the primary one, which differ under per-monitor DPI. Maximized and minimized windows are left
    /// to Windows (moving them would spoil RestoreBounds).
    /// </summary>
    private static void EnsureOnScreen(Window window, IntPtr hwnd, Point target)
    {
        if (window.WindowState != WindowState.Normal || ShellInterop.GetWindowRect(hwnd) is not { } rect)
        {
            return;
        }

        double scale = DpiScaleOf(window);
        if (ShellInterop.IsTitleStripOnAnyMonitor(rect.Left, rect.Top, rect.Width, scale))
        {
            return;
        }

        var area = ShellInterop.GetMonitorArea(target);
        var work = area.Work;

        // Moving to a monitor of another DPI makes Windows scale the window (WM_DPICHANGED): centre the size it will have.
        double ratio = area.Scale / scale;
        double width = Math.Min(rect.Width * ratio, work.Width);
        double height = Math.Min(rect.Height * ratio, work.Height);
        int x = (int)Math.Round(work.Left + ((work.Width - width) / 2));
        int y = (int)Math.Round(work.Top + ((work.Height - height) / 2));
        ShellInterop.MoveWindow(hwnd, x, y);
        AppLog.Info($"display: {window.GetType().Name} was on no monitor ({rect.Left},{rect.Top}); moved to {x},{y}");
    }

    /// <summary>
    /// Makes WPF build the window's render target again: after the output moved to another
    /// adapter it may stop presenting. One frame is drawn in software. Nothing when the process
    /// draws in software anyway.
    /// </summary>
    private void RefreshRendering(IntPtr hwnd)
    {
        if (System.Windows.Media.RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly
            || HwndSource.FromHwnd(hwnd) is not { IsDisposed: false, CompositionTarget: { } target }
            || target.RenderMode == RenderMode.SoftwareOnly)
        {
            return;
        }

        target.RenderMode = RenderMode.SoftwareOnly;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (HwndSource.FromHwnd(hwnd) is { IsDisposed: false, CompositionTarget: { } again })
            {
                again.RenderMode = RenderMode.Default;
            }

            InvalidateVisual();
        });
    }

    /// <summary>
    /// Within 2 minutes of a display change, an activation also puts the window on top of the
    /// z-order (with its dialogs), in case Windows activated it without raising it. Only when it
    /// really is the foreground window: nothing here takes the foreground from another program.
    /// </summary>
    private void OnHookActivated()
    {
        if (_displayChangedAt is not { } changedAt || Environment.TickCount64 - changedAt > RaiseAfterDisplayChangeMs)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ShellInterop.GetForegroundWindow() != hwnd)
        {
            return;
        }

        if (!_raiseLogged)
        {
            _raiseLogged = true;
            AppLog.Info($"display: activated after a change; raised to the top; {ShellInterop.DescribeWindow(hwnd, DpiScaleOf(this))}");
        }

        ShellInterop.RaiseToTop(hwnd);
    }

    // ---- the search box (Ctrl+F) ------------------------------------------------------------------

    /// <summary>The search box is open (typing).</summary>
    private bool IsSearchEditing => SearchBox.Visibility == Visibility.Visible;

    /// <summary>The tab the box searches: when it moves or another tab is shown, the box closes.</summary>
    private PaneViewModel? _searchTab;

    /// <summary>The box is being closed (its lost focus must not close it a second time).</summary>
    private bool _searchEnding;

    /// <summary>
    /// What is typed reaches the list after a short pause: a folder of thousands of items is not
    /// filtered again on every key.
    /// </summary>
    private DispatcherTimer? _searchDelay;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Opens the search box over the chip with the shown tab's search, all of it selected.</summary>
    private void BeginSearch()
    {
        if (!_vm.HasTab || _rename.IsOpen || ActiveList.IsMarqueeRunning)
        {
            return;
        }

        EndAddressEdit();
        _cascade.SetEditing(true); // no hover opens a list over the items, and Esc reaches the box
        if (!IsSearchEditing)
        {
            _searchTab = _vm.ActivePane;
            _searchTab.PropertyChanged += OnSearchTabChanged;
            _vm.PropertyChanged += OnSearchMainChanged;
            SearchEdit.Text = _searchTab.SearchText;
            SearchBox.Visibility = Visibility.Visible;
        }

        SearchEdit.Focus();
        SearchEdit.SelectAll();
    }

    /// <summary>Closes the box; what was typed stays the tab's search (the chip shows it).</summary>
    private void EndSearch()
    {
        if (!IsSearchEditing || _searchEnding)
        {
            return;
        }

        _searchEnding = true;
        try
        {
            ApplySearchNow();
            if (_searchTab is not null)
            {
                _searchTab.PropertyChanged -= OnSearchTabChanged;
                _searchTab = null;
            }

            _vm.PropertyChanged -= OnSearchMainChanged;
            if (SearchEdit.IsKeyboardFocusWithin && IsActive)
            {
                RenameEditor.FocusWindowItself(this); // the keys act on the list again
            }

            SearchBox.Visibility = Visibility.Collapsed; // out of the layout: no layout news for the IME (see MainWindow.xaml)
            _cascade.SetEditing(_rename.IsOpen || IsAddressEditing);
        }
        finally
        {
            _searchEnding = false;
        }
    }

    /// <summary>Hands what is typed to the tab at once (a pending pause is dropped).</summary>
    private void ApplySearchNow()
    {
        _searchDelay?.Stop();
        if (_searchTab is { } tab && tab.SearchText != SearchEdit.Text)
        {
            tab.SearchText = SearchEdit.Text;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsSearchEditing || _searchTab is null)
        {
            return;
        }

        if (_searchDelay is null)
        {
            _searchDelay = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = SearchDelay };
            _searchDelay.Tick += (_, _) => ApplySearchNow();
        }

        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Down:
                e.Handled = true; // the matches are shown: the keys go to the list
                EndSearch();
                break;
            case Key.Escape:
                e.Handled = true; // like Explorer: the search goes away, the whole list is back
                SearchEdit.Text = string.Empty;
                EndSearch();
                break;
        }
    }

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Focus inside the box (its own menu) keeps it open.
        if (!SearchEdit.IsKeyboardFocusWithin)
        {
            EndSearch();
        }
    }

    /// <summary>The searched tab moved (another folder: its search is cleared) or changed its search otherwise.</summary>
    private void OnSearchTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.Location))
        {
            EndSearch();
        }
    }

    private void OnSearchMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActivePane))
        {
            EndSearch();
        }
    }

    private void OnSearchChipClick(object sender, MouseButtonEventArgs e)
    {
        if (!e.Handled && e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            BeginSearch();
        }
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        EndSearch();
        if (_vm.HasTab)
        {
            _vm.ActivePane.SearchText = string.Empty;
        }
    }

    // ---- the address bar ------------------------------------------------------------------------

    /// <summary>The address bar shows its edit box (the path, to type over or copy).</summary>
    private bool IsAddressEditing => AddressEdit.Visibility == Visibility.Visible;

    /// <summary>The tab whose path the edit box holds: when it moves, the edit ends.</summary>
    private PaneViewModel? _addressTab;

    /// <summary>The edit box is being closed (its lost focus must not close it a second time).</summary>
    private bool _addressEnding;

    /// <summary>
    /// Counts the openings and closings of the edit box: an Enter belongs to the one it was pressed in
    /// (a later box is not closed by it, a box closed meanwhile drops what it asked for).
    /// </summary>
    private int _addressSession;

    /// <summary>The session whose Enter is being handled (-1: none): another Enter of that session waits for it (one terminal per Enter).</summary>
    private int _addressCommittingSession = -1;

    /// <summary>A press on the blank part of the address bar (a crumb's own click is handled by its button).</summary>
    private void OnAddressBarClick(object sender, MouseButtonEventArgs e)
    {
        if (!e.Handled && !IsAddressEditing && e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            BeginAddressEdit();
        }
    }

    /// <summary>
    /// Opens the address bar's edit box with the shown folder's path, all of it selected: typing
    /// replaces it, Ctrl+C (or the box's menu) copies it. At a top list the box starts empty.
    /// </summary>
    private void BeginAddressEdit()
    {
        if (!_vm.HasTab || _rename.IsOpen || ActiveList.IsMarqueeRunning)
        {
            return;
        }

        EndSearch();
        _cascade.SetEditing(true); // no hover opens a list over the items, and Esc reaches the box
        if (!IsAddressEditing)
        {
            _addressSession++;
            _addressTab = _vm.ActivePane;
            _addressTab.PropertyChanged += OnAddressTabChanged;
            AddressEdit.Text = _addressTab.CurrentFolderPath ?? string.Empty;

            // The count beside the bar keeps its width while the box is open: a load or a selection
            // that changes the count would otherwise resize the box, and every new layout of the box
            // is news for the IME (TSF), whose answer lays out the whole window under its lock.
            CountPanel.Width = CountPanel.ActualWidth;
            AddressEdit.Visibility = Visibility.Visible;
            Crumbs.Visibility = Visibility.Hidden;
            AddressBar.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        }

        AddressEdit.Focus();
        AddressEdit.SelectAll();
    }

    /// <summary>Closes the edit box (what was typed is dropped); the keys go back to the list.</summary>
    private void EndAddressEdit()
    {
        if (!IsAddressEditing || _addressEnding)
        {
            return;
        }

        _addressEnding = true;
        _addressSession++;
        try
        {
            if (_addressTab is not null)
            {
                _addressTab.PropertyChanged -= OnAddressTabChanged;
                _addressTab = null;
            }

            if (AddressEdit.IsKeyboardFocusWithin && IsActive)
            {
                RenameEditor.FocusWindowItself(this); // Backspace, Delete, Ctrl+C act on the list again
            }

            AddressEdit.Visibility = Visibility.Collapsed; // out of the layout: no layout news for the IME (see MainWindow.xaml)
            Crumbs.Visibility = Visibility.Visible;
            CountPanel.ClearValue(WidthProperty);
            AddressBar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            _cascade.SetEditing(_rename.IsOpen);
        }
        finally
        {
            _addressEnding = false;
        }
    }

    /// <summary>The tab of the edit box moved (a navigation, a reload that followed a rename): its path in the box is stale.</summary>
    private void OnAddressTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.Location))
        {
            EndAddressEdit();
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (!e.IsRepeat && _addressCommittingSession != _addressSession)
                {
                    _ = CommitAddressAsync(AddressEdit.Text);
                }

                break;
            case Key.Escape:
                e.Handled = true;
                EndAddressEdit();
                break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                ToggleAddressWslPath();
                break;
        }
    }

    /// <summary>
    /// Ctrl + Shift + C in the box: its text becomes the same place seen from WSL (<c>C:\temp</c> →
    /// <c>/mnt/c/temp</c>), or back from <c>/mnt/x/…</c>, and is copied to the clipboard (either way;
    /// it stays selected too). The whole text, not the selection. Text that has no other form stays,
    /// said on the message line, and the clipboard is left alone.
    /// </summary>
    private void ToggleAddressWslPath()
    {
        if (WslPath.Toggle(AddressEdit.Text) is not { } other)
        {
            _vm.Message.Info("WSL のパスに変換できません（ドライブ文字のパス・WSL のフォルダ・/mnt/ドライブ/… だけ）。");
            return;
        }

        AddressEdit.Text = other;
        AddressEdit.SelectAll();
        if (ShellActions.CopyPath(other))
        {
            _vm.Message.Success(other.StartsWith('/') ? "WSL のパスに変換してコピーしました。" : "Windows のパスに戻してコピーしました。");
        }
        else
        {
            _vm.Message.Error("パスは変換しましたが、クリップボードにコピーできませんでした。");
        }
    }

    /// <summary>Enter: goes to the typed path or runs the command. When that fails (said on the message line) the box stays, with the text selected.</summary>
    private async Task CommitAddressAsync(string text)
    {
        int session = _addressSession;
        _addressCommittingSession = session;
        bool done;
        try
        {
            // The box closes before a folder loads (not after): a box with the keyboard while thousands
            // of cards are laid out is the IME's way into that layout (see MainWindow.xaml).
            done = await _vm.GoToAddressAsync(text, () => session == _addressSession, leaving: () =>
            {
                if (session == _addressSession)
                {
                    EndAddressEdit();
                }
            });
        }
        finally
        {
            if (_addressCommittingSession == session)
            {
                _addressCommittingSession = -1;
            }
        }

        if (!IsAddressEditing || session != _addressSession)
        {
            return; // ended meanwhile (Esc, another tab, a press elsewhere, the tab moved)
        }

        if (done)
        {
            EndAddressEdit();
        }
        else
        {
            AddressEdit.Focus();
            AddressEdit.SelectAll();
        }
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsAddressEditing || _addressEnding)
        {
            return;
        }

        if (e.NewFocus is DependencyObject next && (RenameEditor.IsWithin(next, AddressEdit) || RenameEditor.IsInContextMenu(next)))
        {
            return; // the box's own parts, or its menu (切り取り / コピー / 貼り付け): focus comes back
        }

        if (e.NewFocus is null && !IsActive)
        {
            return; // another window took over (to copy a path from it): the edit waits for the return
        }

        EndAddressEdit();
    }

    // ---- crumbs as cascade anchors: resting on one lists its subfolders under it -----------------

    /// <summary>
    /// A crumb was pressed: nothing opens under the pointer until it leaves a crumb. The click
    /// navigates, and the new crumb that comes under the still pointer raises a MouseEnter.
    /// </summary>
    private bool _crumbQuiet;

    /// <summary>The tab whose crumbs are watched (<see cref="WatchCrumbs"/>).</summary>
    private PaneViewModel? _crumbTab;

    /// <summary>The crumbs last shown: when they are replaced, each is an anchor that went away.</summary>
    private IReadOnlyList<BreadcrumbItem> _crumbs = [];

    private void OnCrumbMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_crumbQuiet && sender is FrameworkElement { DataContext: BreadcrumbItem crumb } element)
        {
            _cascade.AnchorMouseEnter(element, element, crumb);
        }
    }

    /// <summary>A move without a button held: arms the crumb again after everything was closed under it.</summary>
    private void OnCrumbMouseMove(object sender, MouseEventArgs e)
    {
        if (!_crumbQuiet
            && e.LeftButton == MouseButtonState.Released
            && e.RightButton == MouseButtonState.Released
            && sender is FrameworkElement { DataContext: BreadcrumbItem crumb } element)
        {
            _cascade.AnchorMouseMove(element, element, crumb, e.GetPosition(this));
        }
    }

    private void OnCrumbMouseLeave(object sender, MouseEventArgs e)
    {
        _crumbQuiet = false;
        if (sender is FrameworkElement element)
        {
            _cascade.AnchorMouseLeave(element);
        }
    }

    /// <summary>The element under the pointer belongs to a crumb (its template inherits the crumb as DataContext).</summary>
    private static bool IsOverCrumb(object? source) =>
        source is FrameworkElement { DataContext: BreadcrumbItem } or FrameworkContentElement { DataContext: BreadcrumbItem };

    private void OnCrumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        _crumbQuiet = true;
        _cascade.CloseAll(); // the click navigates (on the last crumb it does nothing): the list goes either way
    }

    /// <summary>
    /// Follows the crumbs of the shown tab. They are rebuilt without input (the path changes, the tab
    /// changes), and an element that goes away raises no MouseLeave: the list under it, or a hover
    /// waiting to open it, is called off here instead.
    /// </summary>
    private void WatchCrumbs(PaneViewModel tab)
    {
        if (_crumbTab is { } old)
        {
            old.PropertyChanged -= OnCrumbTabChanged;
        }

        _crumbTab = tab;
        tab.PropertyChanged += OnCrumbTabChanged;
        DropCrumbs(tab.Breadcrumbs);
    }

    private void OnCrumbTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.Breadcrumbs) && sender is PaneViewModel tab)
        {
            DropCrumbs(tab.Breadcrumbs);
        }
    }

    private void DropCrumbs(IReadOnlyList<BreadcrumbItem> next)
    {
        foreach (var crumb in _crumbs)
        {
            _cascade.AnchorRemoved(crumb);
        }

        _crumbs = next;
    }

    // ---- panes: one PaneView per pane of the shown tab --------------------------------------------

    /// <summary>Every pane view made so far (at most <see cref="PaneRules.MaxPanes"/>), kept for the window's life.</summary>
    private readonly List<PaneView> _paneViews = [];

    /// <summary>The views on screen, by the pane they show (the panes of <see cref="MainViewModel.ShownLayout"/>).</summary>
    private Dictionary<PaneViewModel, PaneView> _shownViews = [];

    /// <summary>The layout the views are laid out by (to tell a moved splitter from a new shape).</summary>
    private PaneLayout<PaneViewModel>? _builtLayout;

    /// <summary>The split each splitter on screen drags (rebuilt with the layout).</summary>
    private readonly Dictionary<GridSplitter, PaneSplit<PaneViewModel>> _splitOf = [];

    /// <summary>The view of the active pane (the first view while nothing is built yet).</summary>
    private PaneView ActivePaneView =>
        _shownViews.TryGetValue(_vm.ActivePane, out var view) ? view : _paneViews.Count > 0 ? _paneViews[0] : NewPaneView();

    /// <summary>
    /// Lays the views out by the shown tab's layout (<see cref="MainViewModel.ShownLayout"/>). A view
    /// stays with its pane while the pane stays on screen; a pane new on screen gets a free view (a
    /// tab of one pane always the same one, without moving it: as the one grid of the window before
    /// panes). Only a moved splitter (the same panes, the same shape) updates the sizes in place;
    /// anything else rebuilds the splits, and the views that moved get their scroll positions back.
    /// </summary>
    private void BuildPanes()
    {
        var layout = _vm.ShownLayout;
        if (_builtLayout is { } built && SameShape(built.Root, layout.Root))
        {
            _builtLayout = layout;
            UpdateSplits(layout.Root);
            MarkActivePane();
            return;
        }

        var shown = new Dictionary<PaneViewModel, PaneView>();
        foreach (var pane in layout.Panes)
        {
            if (_shownViews.TryGetValue(pane, out var kept))
            {
                shown[pane] = kept;
            }
        }

        var free = new Queue<PaneView>(_paneViews.Where(v => !shown.ContainsValue(v)));
        foreach (var pane in layout.Panes.Where(p => !shown.ContainsKey(p)))
        {
            var view = free.Count > 0 ? free.Dequeue() : NewPaneView();
            view.DataContext = pane;
            shown[pane] = view;
        }

        // Views that move to another split lose their layout: their positions come back after it.
        var offsets = shown.Where(s => _shownViews.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value.ActiveList.VerticalOffset);

        _shownViews = shown;
        _builtLayout = layout;
        _splitOf.Clear();
        foreach (var view in free)
        {
            Detach(view);
            view.DataContext = null; // its cards go with its pane
        }

        if (layout.Root is PaneLeaf<PaneViewModel> { Pane: var only } && ReferenceEquals(PaneArea.Child, shown[only]))
        {
            // the same view as before, the same place: only its pane changed (a tab switch)
        }
        else
        {
            var root = Build(layout.Root);
            if (!ReferenceEquals(PaneArea.Child, root))
            {
                Detach(root);
                PaneArea.Child = root;
            }
        }

        foreach (var (pane, offset) in offsets)
        {
            shown[pane].ScrollWhenReady(offset);
        }

        MarkActivePane();
    }

    private UIElement Build(PaneNode<PaneViewModel> node)
    {
        if (node is PaneLeaf<PaneViewModel> leaf)
        {
            var view = _shownViews[leaf.Pane];
            Detach(view);
            return view;
        }

        var split = (PaneSplit<PaneViewModel>)node;
        bool columns = split.Axis == SplitAxis.Columns;
        var grid = new Grid();
        var splitter = new GridSplitter
        {
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = false,
            Cursor = columns ? Cursors.SizeWE : Cursors.SizeNS,
            ToolTip = "ドラッグでペインの大きさを変更",
        };
        splitter.SetResourceReference(BackgroundProperty, "Brush.Border");
        splitter.DragStarted += OnPaneSplitterDragStarted;
        splitter.DragCompleted += OnPaneSplitterDragCompleted;
        _splitOf[splitter] = split;
        var first = Build(split.First);
        var second = Build(split.Second);

        // A view is reused across layouts: the cell it had in its last grid must not carry over (a
        // former second part keeps Grid.Column / Grid.Row = 2 and would cover the other part, leaving
        // its own cell empty and dead to clicks).
        foreach (var part in new[] { first, second })
        {
            part.ClearValue(Grid.ColumnProperty);
            part.ClearValue(Grid.RowProperty);
        }

        if (columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PaneRules.SplitterThickness) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PaneRules.SplitterThickness) });
            grid.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
        }

        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        SetRatio(grid, split);
        return grid;
    }

    /// <summary>The first part's share of a split grid as star sizes (the splitter between them is fixed).</summary>
    private static void SetRatio(Grid grid, PaneSplit<PaneViewModel> split)
    {
        var a = new GridLength(split.Ratio, GridUnitType.Star);
        var b = new GridLength(1 - split.Ratio, GridUnitType.Star);
        if (split.Axis == SplitAxis.Columns)
        {
            grid.ColumnDefinitions[0].Width = a;
            grid.ColumnDefinitions[2].Width = b;
        }
        else
        {
            grid.RowDefinitions[0].Height = a;
            grid.RowDefinitions[2].Height = b;
        }
    }

    /// <summary>The same panes in the same places (only ratios may differ).</summary>
    private static bool SameShape(PaneNode<PaneViewModel> a, PaneNode<PaneViewModel> b) => (a, b) switch
    {
        (PaneLeaf<PaneViewModel> x, PaneLeaf<PaneViewModel> y) => ReferenceEquals(x.Pane, y.Pane),
        (PaneSplit<PaneViewModel> x, PaneSplit<PaneViewModel> y) => x.Axis == y.Axis && SameShape(x.First, y.First) && SameShape(x.Second, y.Second),
        _ => false,
    };

    /// <summary>A splitter moved: the grids on screen take the new layout's nodes and ratios (they are new objects).</summary>
    private void UpdateSplits(PaneNode<PaneViewModel> root)
    {
        var byGrid = _splitOf.ToDictionary(s => (Grid)s.Key.Parent, s => s.Key);
        _splitOf.Clear();
        Walk(PaneArea.Child, root);

        void Walk(UIElement? element, PaneNode<PaneViewModel> node)
        {
            if (node is not PaneSplit<PaneViewModel> split || element is not Grid grid || !byGrid.TryGetValue(grid, out var splitter))
            {
                return;
            }

            _splitOf[splitter] = split;
            SetRatio(grid, split);
            Walk(grid.Children[0], split.First);
            Walk(grid.Children[2], split.Second);
        }
    }

    private static void Detach(UIElement element)
    {
        switch (VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
        }
    }

    private PaneView NewPaneView()
    {
        var view = new PaneView();
        view.ActivationRequested += v =>
        {
            if (v.Pane is { } pane)
            {
                _vm.ActivatePane(pane);
            }
        };

        // A list's scrolling only concerns what hangs on its own items.
        view.CardGrid.Scrolled += (_, _) => _cascade.CloseCardCascade();
        view.CardGrid.CardSizeStepRequested += step => _vm.StepCardSize(step);
        view.DetailsList.Scrolled += (_, _) => _cascade.CloseCardCascade();

        // The press on a column title made the pane active (PaneView.ActivationRequested): its sort mode changes.
        view.DetailsList.HeaderClicked += column => _vm.SetSortMode(CardSortModes.Next(_vm.ActivePane.SortMode, column));
        view.DetailsList.ColumnResized += (key, width) => _vm.SetDetailsColumnWidth(key, width);

        // The header's right click: Explorer's column chooser.
        view.DetailsList.HeaderMenuRequested += target =>
        {
            _cascade.CloseAll();
            _menus.ShowDetailsColumns(target, _columnTitles);
        };
        if (_columnTitles is not null)
        {
            view.DetailsList.SetExtraTitles(_columnTitles);
        }

        _paneViews.Add(view);
        return view;
    }

    /// <summary>
    /// Each view's look: whether the window is split, the frame of the active pane, and the pane's
    /// colour, the same as its tab's (<see cref="TabLayoutViewModel.PaneSlot"/>).
    /// </summary>
    private void MarkActivePane()
    {
        bool split = _shownViews.Count > 1;
        foreach (var (pane, view) in _shownViews)
        {
            view.IsSplit = split;
            view.ShowsEmptyHint = _vm.IsEmptyPane(pane);
            view.IsActive = ReferenceEquals(pane, _vm.ActivePane);
            view.Slot = split ? _vm.SlotOf(pane) : -1;
        }
    }

    /// <summary>The pane view <paramref name="node"/> is in, or null (outside the panes: the toolbar, the sidebar, a popup).</summary>
    private PaneView? PaneViewOf(DependencyObject? node)
    {
        for (; node is not null; node = ParentOf(node))
        {
            if (node is PaneView view)
            {
                return _shownViews.ContainsValue(view) ? view : null;
            }
        }

        return null;
    }

    /// <summary><paramref name="node"/> is in the items area of <paramref name="view"/> (not its notice or banner).</summary>
    private static bool IsInItemsHost(PaneView view, DependencyObject? node)
    {
        for (; node is not null && !ReferenceEquals(node, view); node = ParentOf(node))
        {
            if (ReferenceEquals(node, view.ItemsHost))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A cascade opened from an item of a pane was clicked: that pane becomes active (nothing for
    /// other anchors). The cascade stays: it is that pane's, and a right click on its row opens a menu over it.
    /// </summary>
    private void ActivatePaneOf(FrameworkElement element)
    {
        if (PaneViewOf(element)?.Pane is { } pane)
        {
            _vm.ActivatePane(pane, closeCardCascade: false);
        }
    }

    /// <summary>
    /// <paramref name="source"/> is in a pane but above its items (its notice or error banner): not
    /// the blank area (no clearing of the selection, no marquee, no blank menu), as when they were
    /// above the items area of the window.
    /// </summary>
    private bool IsOnPaneBanner(object? source)
    {
        var node = source as DependencyObject;
        return PaneViewOf(node) is { } view && !IsInItemsHost(view, node);
    }

    /// <summary>
    /// A splitter is grabbed: neither side can be dragged below what its panes need
    /// (<see cref="PaneRules.MinExtent"/>), unless the window is too small for both, then the
    /// splitter moves freely (the panes share what there is).
    /// </summary>
    private void OnPaneSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        _cascade.CloseAll();
        if (sender is not GridSplitter { Parent: Grid grid } splitter || !_splitOf.TryGetValue(splitter, out var split))
        {
            return;
        }

        bool columns = split.Axis == SplitAxis.Columns;
        double extent = columns ? grid.ActualWidth : grid.ActualHeight;
        double first = PaneRules.MinExtent(split.First, split.Axis);
        double second = PaneRules.MinExtent(split.Second, split.Axis);
        if (first + second + PaneRules.SplitterThickness > extent)
        {
            return;
        }

        if (columns)
        {
            grid.ColumnDefinitions[0].MinWidth = first;
            grid.ColumnDefinitions[2].MinWidth = second;
        }
        else
        {
            grid.RowDefinitions[0].MinHeight = first;
            grid.RowDefinitions[2].MinHeight = second;
        }
    }

    /// <summary>A splitter is let go: the split keeps its new ratio (saved with the tab).</summary>
    private void OnPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid grid } splitter || !_splitOf.TryGetValue(splitter, out var split))
        {
            return;
        }

        bool columns = split.Axis == SplitAxis.Columns;
        double a = columns ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
        double b = columns ? grid.ColumnDefinitions[2].ActualWidth : grid.RowDefinitions[2].ActualHeight;
        if (columns)
        {
            grid.ColumnDefinitions[0].MinWidth = 0;
            grid.ColumnDefinitions[2].MinWidth = 0;
        }
        else
        {
            grid.RowDefinitions[0].MinHeight = 0;
            grid.RowDefinitions[2].MinHeight = 0;
        }

        if (a + b <= 0)
        {
            return;
        }

        double extent = a + b + PaneRules.SplitterThickness;
        _vm.SetSplitRatio(split, PaneRules.ClampRatio(split, extent, a / (a + b)));
    }

    // ---- IMainView ----------------------------------------------------------------------------

    public void CloseCascade() => _cascade.CloseAll();

    public void CloseCardCascade() => _cascade.CloseCardCascade();

    public double GetScrollOffset() => ActiveList.VerticalOffset;

    public void ScrollTo(double offset) => ActiveList.ScrollTo(offset);

    public double GetScrollOffset(PaneViewModel pane) =>
        _shownViews.TryGetValue(pane, out var view) ? view.ActiveList.VerticalOffset : pane.SavedScrollOffset;

    public void ScrollTo(PaneViewModel pane, double offset)
    {
        if (_shownViews.TryGetValue(pane, out var view))
        {
            view.ScrollWhenReady(offset);
        }
    }

    public nint WindowHandle => new WindowInteropHelper(this).Handle;

    public bool Confirm(string title, string body, string? path, string primaryText, bool danger)
    {
        _cascade.CloseAll();
        var dialog = new ConfirmDialog(title, body, path, primaryText, danger) { Owner = this };
        _modalDepth++;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            _modalDepth--;
        }
    }

    public string? AskName(string title, string body, string initial)
    {
        _cascade.CloseAll();
        var dialog = new NameInputDialog(title, body, initial) { Owner = this };
        _modalDepth++;
        try
        {
            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
        finally
        {
            _modalDepth--;
        }
    }

    // (a tab dragged from the strip onto the panes, a folder onto an empty pane: MainWindow.PaneDrag.cs)

    // ---- auto refresh: what a replaced item list would break ------------------------------------

    /// <summary>Dialogs of the window are open (ShowDialog runs a nested loop in which the auto refresh's timer still ticks).</summary>
    private int _modalDepth;

    /// <summary>The last keyboard, mouse, wheel or drag input over the window (<c>Environment.TickCount64</c>).</summary>
    private long _lastInputAt;

    /// <summary>The auto refresh leaves the list alone this long after input (a click about to land, a hover about to open).</summary>
    private const long AutoRefreshQuietMs = 800;

    private Point _lastPointer = new(double.NaN, double.NaN);

    private void NoteInput() => _lastInputAt = Environment.TickCount64;

    bool IMainView.IsAutoRefreshBlocked =>
        WindowState == WindowState.Minimized
        || IsNameBusy
        || IsAddressEditing // a list swapped under the edit box is the IME's way into a big layout (see MainWindow.xaml)
        || _shownViews.Values.Any(v => v.ActiveList.IsMarqueeRunning)
        || _cascade.IsOpen || _cascade.IsMenuOpen || _cascade.IsHoverPending
        || _modalDepth > 0
        || Mouse.LeftButton == MouseButtonState.Pressed
        || Mouse.RightButton == MouseButtonState.Pressed
        || Mouse.MiddleButton == MouseButtonState.Pressed
        || Environment.TickCount64 - _lastInputAt < AutoRefreshQuietMs;

    public IReadOnlyList<string>? PickFolders()
    {
        _cascade.CloseAll();
        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "登録するフォルダを選択",
        };
        _modalDepth++;
        try
        {
            return dialog.ShowDialog(this) == true ? dialog.FolderNames : null;
        }
        finally
        {
            _modalDepth--;
        }
    }

    /// <summary>The list of every shortcut (F1 / Ctrl + /), modal over the window as the other dialogs.</summary>
    private void ShowShortcutHelp()
    {
        _cascade.CloseAll();
        var window = new ShortcutHelpWindow { Owner = this };
        if (ActualHeight > 0)
        {
            window.MaxHeight = ActualHeight; // the list scrolls beyond that
        }

        _modalDepth++;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _modalDepth--;
        }
    }

    public bool ShowSettings(SettingsViewModel settings)
    {
        _cascade.CloseAll();

        // No taller than the main window (it fits its height to the settings, which scroll beyond that).
        var window = new SettingsWindow(settings) { Owner = this };
        if (ActualHeight > 0)
        {
            window.MaxHeight = ActualHeight;
        }

        _modalDepth++;
        try
        {
            return window.ShowDialog() == true;
        }
        finally
        {
            _modalDepth--;
        }
    }

    public void ApplyTheme(AppTheme theme)
    {
        _cascade.CloseAll();
        _theme.Apply(theme); // swaps the colour dictionary and the title bars
    }

    public void ApplyTextScale(int percent)
    {
        _cascade.CloseAll(); // its lists are placed by the rows, which change height
        TextScale.Apply(percent);
    }
}
