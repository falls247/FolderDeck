using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;
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
        };

        Grid.Scrolled += (_, _) => _cascade.CloseCardCascade();
        Grid.CardSizeStepRequested += step => vm.StepCardSize(step);

        // The details list: like the grid, its scrolling only concerns what hangs on its own rows.
        Details.Scrolled += (_, _) => _cascade.CloseCardCascade();
        Details.HeaderClicked += column => vm.SetSortMode(CardSortModes.Next(vm.SelectedTab.SortMode, column));
        Details.ColumnResized += (key, width) => vm.SetDetailsColumnWidth(key, width);

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

        // §9.6 rule 5: these close every flyout at once (Popups are topmost windows).
        Deactivated += (_, _) => _cascade.CloseAll();
        LocationChanged += (_, _) => _cascade.CloseAll();
        SizeChanged += (_, _) => _cascade.CloseAll();
        StateChanged += (_, _) => _cascade.CloseAll();
        Application.Current.Deactivated += (_, _) => _cascade.CloseAll();

        SourceInitialized += (_, _) => _theme.ApplyTitleBar(this);
        SourceInitialized += (_, _) => WatchWindowMessages();
        Activated += OnActivatedReadDrives;
        Activated += (_, _) => _ = vm.OnActivatedAsync(); // a cut of ours may have been pasted in Explorer
        Closed += (_, _) => _hook?.Dispose();
        theme.ThemeChanged += () => _theme.ApplyTitleBar(this);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Closing += OnClosing;

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
        if (_vm.IsFileOperationRunning
            && !Confirm("終了しますか？", "ファイルの移動・コピー・削除が実行中です。終了すると途中で中断されます。", null, "終了する", danger: true))
        {
            e.Cancel = true;
            return;
        }

        _vm.SaveState(CapturePlacement());
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
            var strip = new Rect(left * scale, top * scale, Math.Max(100, placement.Width * scale), 40 * scale);
            visible = ShellInterop.IsOnAnyMonitor(strip);
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

        switch (key)
        {
            case Key.F5:
                e.Handled = true;
                _vm.RefreshCommand.Execute(null);
                break;
            case Key.Back when !inTextBox && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                _vm.GoUpCommand.Execute(null);
                break;
            case Key.W when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
                e.Handled = true; // like Explorer: closes the shown tab, whatever its kind
                _vm.CloseTabCommand.Execute(_vm.SelectedTab);
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
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
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
    /// The selection keys (not in a text box): Ctrl+A, Esc (after the cascade had its turn),
    /// Ctrl+C / Ctrl+X / Ctrl+V and Delete. Copy, cut and delete act on the selection only, never on
    /// the item under the pointer (nor the row the cascade's keys highlight); a held key repeats
    /// nothing (one paste or one delete confirmation per press). Delete only recycles: Shift+Delete
    /// (Explorer's permanent delete) and Ctrl+Delete are not used. Returns true when the key was used.
    /// </summary>
    private bool HandleSelectionKey(Key key, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            return _vm.ClearSelection();
        }

        if (key == Key.Delete && modifiers == ModifierKeys.None)
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
                    _vm.Message.Info(_vm.HasTab && _vm.SelectedTab.FolderTargetPath is not null && _vm.SelectedTab.IsLoading
                        ? "フォルダの読み込みが終わってから貼り付けてください。"
                        : "この一覧には貼り付けできません。");
                }

                break;
        }

        return true;
    }

    /// <summary>
    /// A left press in the item area clears the selection, unless it is on an item (its click
    /// decides on release), on a scroll bar, a button or a grip, or a Ctrl / Shift click is meant.
    /// </summary>
    private void OnContentLeftDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 || IsOnItemOrControl(e.OriginalSource))
        {
            return;
        }

        _vm.ClearSelection();
    }

    /// <summary>
    /// A right click on the blank area that nothing else took: 貼り付け and 最新の情報に更新. A click
    /// that only reached the blank area because the items view does not hit test (while the tab
    /// loads) but lies on a realized item still gets that item's menu.
    /// </summary>
    private void OnContentRightUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || !_vm.HasTab || IsOnItemOrControl(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        if (ActiveList.RealizedItemAt(e) is { } view
            && ((view as CardView)?.ViewModel ?? (view as DetailsRowView)?.ViewModel) is { } item)
        {
            // No path in the log: the type of the source and the load state tell why it fell through.
            FolderDeck.Core.Infrastructure.AppLog.Info(
                $"Item right click reached the blank area: source {e.OriginalSource?.GetType().Name ?? "null"}, "
                + $"view {view.GetType().Name}, loading {_vm.SelectedTab.IsLoading}");
            ItemContextMenu(item, view);
            return;
        }

        _cascade.CloseAll();
        _menus.ShowBlank(ContentHost);
    }

    /// <summary>The element pressed is (inside) a card, a details row, a scroll bar, a button or a grip.</summary>
    private static bool IsOnItemOrControl(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is CardView or DetailsRowView or ScrollBar or ButtonBase or Thumb)
            {
                return true;
            }
        }

        return false;

        // Text runs are not visuals: walk them up through the logical tree.
        static DependencyObject? Parent(DependencyObject node) =>
            node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }

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
            DropHint.Visibility = Visibility.Collapsed;
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
        var current = _vm.SelectedTab.Filter;
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
        var current = _vm.SelectedTab.SortMode;
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

    // ---- drag & drop registration (§10.1, P1) -----------------------------------------------

    // Folders dropped from Explorer anywhere on the window are registered (the hint shows over the
    // sidebar, where they land; no hint while the sidebar is hidden). Drags the app started itself (cards, rows, previews, sidebar rows)
    // carry a file drop too, for other apps: released over the window they are never a registration.
    private static bool CanDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && !IsOwnDrag(e.Data);

    private static bool IsOwnDrag(IDataObject data) => FileDragOut.IsDragging || FileDrag.IsFromThisApp(data.GetDataPresent);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = CanDrop(e);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok && !_vm.IsSidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!CanDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        e.Handled = true;
        await _vm.RegisterDroppedAsync(paths);
    }

    // ---- ICardHost ----------------------------------------------------------------------------

    /// <summary>
    /// The view that shows the items of the tab right now, by the view mode: the card grid or the
    /// details list. Scroll position and reorder drag go through it. The other one is hidden and
    /// has no items (MainWindow.xaml), so its scroll position is never read or set.
    /// </summary>
    private IItemsView ActiveList => CardShapes.IsCards(_vm.CardShape) ? Grid : Details;

    public void CardMouseEnter(CardView card) => _cascade.CardMouseEnter(card);

    public void CardMouseLeave(CardView card) => _cascade.CardMouseLeave(card);

    /// <summary>
    /// A row of the details list: the name column is both what the pointer rests on and what the
    /// list is placed beside (at the column's right edge), so resting on the other columns opens
    /// nothing. Only a folder has a list; on any other row nothing is armed and a list that is open
    /// closes as on leaving its row.
    /// </summary>
    public void RowMouseEnter(FrameworkElement nameColumn, CardViewModel vm)
    {
        if (CardAnchor.For(vm) is { } anchor)
        {
            _cascade.AnchorMouseEnter(nameColumn, nameColumn, anchor);
        }
    }

    /// <summary>The pointer moved on a name column without a button held: arms the row again after everything was closed under it.</summary>
    public void RowMouseMove(FrameworkElement nameColumn, CardViewModel vm)
    {
        if (CardAnchor.For(vm) is { } anchor)
        {
            _cascade.AnchorMouseMove(nameColumn, nameColumn, anchor, Mouse.GetPosition(this));
        }
    }

    public void RowMouseLeave(FrameworkElement nameColumn) => _cascade.AnchorMouseLeave(nameColumn);

    /// <summary>
    /// A click with Ctrl and / or Shift selects (Ctrl flips the item, Shift takes the range, both add
    /// the range); a plain click clears the selection and opens the item.
    /// </summary>
    public void ItemClick(CardViewModel vm, FrameworkElement element, ModifierKeys modifiers)
    {
        _cascade.CloseAll();
        if (_vm.ClickSelect(vm, modifiers))
        {
            return;
        }

        _vm.ClearSelection();
        _ = _vm.ActivateCardAsync(vm);
    }

    /// <summary>
    /// The menu of an item that is part of a selection of several applies to the whole selection;
    /// a right click outside the selection clears it and shows the item's own menu.
    /// </summary>
    public void ItemContextMenu(CardViewModel vm, FrameworkElement element)
    {
        var item = vm.ToItemRef();
        var targets = _vm.SelectionTargets(item);
        if (targets.Count == 1 && !_vm.SelectedTab.Selection.Contains(vm.Path))
        {
            _vm.ClearSelection();
        }

        _menus.Show(item, element, targets);
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
        int? index;
        _cascade.BeginDrag();
        try
        {
            index = Sidebar.RunReorderDrag(item, source => FileDragOut.Run(source, data, "sidebar row"));
        }
        finally
        {
            _cascade.EndDrag();
        }

        if (index is { } i)
        {
            _vm.MoveRegistered(item, i);
        }
    }

    public bool CanDragItem(CardViewModel vm) => !vm.IsDisabled && FileDragOut.CanDrag(vm.Path);

    /// <summary>
    /// A drag of an item that is part of the selection carries the whole selection (in display
    /// order) to other apps, else the item alone. At the top level of a tab a drag of one card also
    /// carries the reorder format: released over the list it moves the card, released over another
    /// app that app gets the file or folder. Files are never moved inside the app by a drag.
    /// </summary>
    public void ItemDrag(CardViewModel vm)
    {
        if (!CanDragItem(vm))
        {
            return;
        }

        var targets = _vm.SelectionTargets(vm.ToItemRef());
        var paths = targets.Select(t => t.Path).Where(FileDragOut.CanDrag).ToList();
        bool reorder = targets.Count == 1 && _vm.CanReorder(vm);
        if (FileDragOut.CreateData(paths, reorder) is not { } data)
        {
            return;
        }

        var list = ActiveList; // the view the drag started in, also once it has ended
        int? index = null;
        _cascade.BeginDrag();
        try
        {
            if (reorder)
            {
                index = list.RunReorderDrag(vm, source => FileDragOut.Run(source, data, "top card"));
            }
            else
            {
                FileDragOut.Run(this, data, targets.Count > 1 ? "selection" : "card"); // the window outlives any item container
            }
        }
        finally
        {
            _cascade.EndDrag();
        }

        if (index is { } i)
        {
            double offset = list.VerticalOffset;
            _vm.MoveCard(vm, i);
            list.ScrollTo(offset); // the list rebuilds its rows: stay where the item was dropped
        }
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

    // ---- IMainView ----------------------------------------------------------------------------

    public void CloseCascade() => _cascade.CloseAll();

    public void CloseCardCascade() => _cascade.CloseCardCascade();

    public double GetScrollOffset() => ActiveList.VerticalOffset;

    public void ScrollTo(double offset) => ActiveList.ScrollTo(offset);

    public nint WindowHandle => new WindowInteropHelper(this).Handle;

    public bool Confirm(string title, string body, string? path, string primaryText, bool danger)
    {
        _cascade.CloseAll();
        var dialog = new ConfirmDialog(title, body, path, primaryText, danger) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    public IReadOnlyList<string>? PickFolders()
    {
        _cascade.CloseAll();
        var dialog = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "登録するフォルダを選択",
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderNames : null;
    }

    public bool ShowSettings(SettingsViewModel settings)
    {
        _cascade.CloseAll();
        var window = new SettingsWindow(settings) { Owner = this };
        return window.ShowDialog() == true;
    }

    public void ApplyTheme(AppTheme theme)
    {
        _cascade.CloseAll();
        _theme.Apply(theme); // swaps the colour dictionary and the title bars
    }
}
