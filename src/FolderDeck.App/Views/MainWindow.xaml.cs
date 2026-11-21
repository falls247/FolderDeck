using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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

        // The stores are loaded before the window is created: the sidebar opens at its saved width.
        SidebarColumn.Width = new GridLength(AppSettings.ClampSidebarWidth(vm.Services.Stores.Settings.Current.SidebarWidth));

        Grid.Scrolled += (_, _) => _cascade.CloseAll();
        Grid.CardSizeStepRequested += step => vm.StepCardSize(step);

        Sidebar.ItemEntered += (row, item) => _cascade.SidebarMouseEnter(row, item);
        Sidebar.ItemLeft += row => _cascade.SidebarMouseLeave(row);
        Sidebar.ItemClicked += OnSidebarClicked;
        Sidebar.ItemContextMenu += (item, row) => _menus.Show(item.ToItemRef(), row);
        Sidebar.ItemExplorerRequested += OnSidebarExplorer;
        Sidebar.ItemDragRequested += OnSidebarDrag;
        Sidebar.Scrolled += () => _cascade.CloseAll();
        TabStrip.Interacted += () => _cascade.CloseAll();

        // §9.6 rule 5: these close every flyout at once (Popups are topmost windows).
        Deactivated += (_, _) => _cascade.CloseAll();
        LocationChanged += (_, _) => _cascade.CloseAll();
        SizeChanged += (_, _) => _cascade.CloseAll();
        StateChanged += (_, _) => _cascade.CloseAll();
        Application.Current.Deactivated += (_, _) => _cascade.CloseAll();

        SourceInitialized += (_, _) => _theme.ApplyTitleBar(this);
        theme.ThemeChanged += () => _theme.ApplyTitleBar(this);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Closing += (_, _) => vm.SaveState(CapturePlacement());
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

        if (_cascade.IsOpen && (!inTextBox || key == Key.Escape) && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_cascade.HandleKey(key))
            {
                e.Handled = true;
                return;
            }
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
            case Key.W when Keyboard.Modifiers == ModifierKeys.Control && _vm.SelectedTab.IsFolderTab:
                e.Handled = true; // like Explorer: closes the shown folder tab
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

    private void OnRegisterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.RegisterCommand.Execute(null);
        }
    }

    private void OnHideMessage(object sender, RoutedEventArgs e) => _vm.Message.Hide();

    // The popups of the cascade are placed in screen coordinates: they close before the sidebar moves.
    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e) => _cascade.CloseAll();

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => _vm.SidebarWidth = SidebarColumn.ActualWidth;

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
    /// Toolbar sort button: a menu of the five modes, the current one checked, and on 最近の履歴 /
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
    // sidebar, where they land). Drags the app started itself (cards, rows, previews, sidebar rows)
    // carry a file drop too, for other apps: released over the window they are never a registration.
    private static bool CanDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && !IsOwnDrag(e.Data);

    private static bool IsOwnDrag(IDataObject data) => FileDragOut.IsDragging || FileDrag.IsFromThisApp(data.GetDataPresent);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = CanDrop(e);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
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

    public void CardMouseEnter(CardView card) => _cascade.CardMouseEnter(card);

    public void CardMouseLeave(CardView card) => _cascade.CardMouseLeave(card);

    public void CardActivate(CardView card)
    {
        if (card.ViewModel is not { } vm)
        {
            return;
        }

        _cascade.CloseAll();
        _ = _vm.ActivateCardAsync(vm);
    }

    public void CardContextMenu(CardView card)
    {
        if (card.ViewModel is { } vm)
        {
            _menus.Show(vm.ToItemRef(), card);
        }
    }

    public void CardRemove(CardView card)
    {
        if (card.ViewModel is { } vm)
        {
            _vm.RemoveCard(vm);
        }
    }

    public void CardUnpin(CardView card)
    {
        if (card.ViewModel is { IsDisabled: false } vm && _vm.IsPinned(vm.Path))
        {
            _cascade.CloseAll(); // the card may move to its sorted place
            _vm.TogglePin(vm.ToItemRef());
        }
    }

    public void CardShowInExplorer(CardView card)
    {
        if (card.ViewModel is { IsDisabled: false } vm)
        {
            _cascade.CloseAll();
            _ = _vm.ShowInExplorerAsync(vm.ToItemRef());
        }
    }

    // ---- sidebar (登録フォルダ) ----------------------------------------------------------------

    private void OnSidebarClicked(SidebarItemViewModel item)
    {
        _cascade.CloseAll();
        _ = _vm.OpenRegisteredAsync(item);
    }

    private void OnSidebarExplorer(SidebarItemViewModel item)
    {
        if (!item.IsDisabled)
        {
            _cascade.CloseAll();
            _ = _vm.ShowInExplorerAsync(item.ToItemRef());
        }
    }

    /// <summary>
    /// A sidebar row is dragged: released over the sidebar it moves the row, released over another
    /// app that app gets the folder (a missing folder can only be moved).
    /// </summary>
    private void OnSidebarDrag(SidebarItemViewModel item)
    {
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

    public bool CanDragCard(CardView card) => card.ViewModel is { IsDisabled: false } vm && FileDragOut.CanDrag(vm.Path);

    /// <summary>
    /// At the top level of a tab one drag carries both the reorder format and the file drop: released
    /// over the grid it moves the card, released over another app that app gets the file or folder.
    /// Inside folders the drag only hands the item to other apps.
    /// </summary>
    public void CardDrag(CardView card)
    {
        if (card.ViewModel is not { } vm || !CanDragCard(card))
        {
            return;
        }

        bool reorder = _vm.CanReorder(vm);
        if (FileDragOut.CreateData([vm.Path], reorder) is not { } data)
        {
            return;
        }

        int? index = null;
        _cascade.BeginDrag();
        try
        {
            if (reorder)
            {
                index = Grid.RunReorderDrag(vm, source => FileDragOut.Run(source, data, "top card"));
            }
            else
            {
                FileDragOut.Run(this, data, "card"); // the window outlives any card container
            }
        }
        finally
        {
            _cascade.EndDrag();
        }

        if (index is { } i)
        {
            double offset = Grid.VerticalOffset;
            _vm.MoveCard(vm, i);
            Grid.ScrollTo(offset); // the grid rebuilds its rows: stay where the card was dropped
        }
    }

    // ---- IMainView ----------------------------------------------------------------------------

    public void CloseCascade() => _cascade.CloseAll();

    public double GetScrollOffset() => Grid.VerticalOffset;

    public void ScrollTo(double offset) => Grid.ScrollTo(offset);

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
}
