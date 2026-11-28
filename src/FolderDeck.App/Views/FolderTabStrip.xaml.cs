using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FolderDeck.Core.Transfer;
using FolderDeck.Core.Layout;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The tab strip (every open tab: 最近の履歴, よく使う項目 and the folder tabs): a press shows the
/// tab, dragging it sideways moves it among the others (they swap as the pointer passes the middle
/// of a neighbour), dragging it down onto a pane of the group on screen puts it in that pane
/// (<see cref="TabDraggedOverPanes"/> / <see cref="TabDroppedOnPanes"/>), a middle click or × closes it.
/// A press on a tab that is not on screen (outside the group on screen, or any other tab than the one
/// shown alone) shows it only when the button is released without a drag (shown at once, what is on
/// screen would leave it before the tab could be dropped on a pane or a snap layout).
/// The bar over a pane group's tabs, dragged, moves the whole group past the tabs and groups beside it.
/// A drag shows a see-through picture of what it holds under the pointer (<see cref="DragGhost"/>). When the tabs do not fit, ◀ / ▶ appear on the
/// left (next to the sidebar button) and step one tab at a time, and the wheel (vertical or
/// horizontal) over the strip scrolls it; there is no scroll bar. Resting on a tab that is not shown
/// lists its folder under it: the hover events go to the main window's cascade (<see cref="TabEntered"/>).
/// </summary>
public partial class FolderTabStrip : UserControl
{
    private MainViewModel? _vm;
    private TabLayoutViewModel? _pressed;
    private Point _pressedAt;
    private bool _moving;

    /// <summary>The pressed tab is shown on release (it is outside the group on screen; see the class summary).</summary>
    private bool _selectOnRelease;

    /// <summary>The dragged tab is over the panes (not the strip): moving it does not reorder the strip.</summary>
    private bool _overPanes;

    /// <summary>The press was on the bar over a pane group: the drag moves the whole group (<see cref="MoveGroupTowards"/>).</summary>
    private bool _groupDrag;

    /// <summary>The picture under the pointer while a drag runs.</summary>
    private DragGhost? _ghost;

    public FolderTabStrip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Tabs.PreviewMouseLeftButtonDown += OnLeftDown;
        Tabs.MouseMove += OnMouseMove;
        Tabs.PreviewMouseLeftButtonUp += (_, e) => EndPress(e);
        Tabs.LostMouseCapture += (_, _) =>
        {
            // Taken away mid-drag (Alt+Tab, a dialog): the moves so far stand, no drop on a pane.
            if (_moving && _pressed is { } tab)
            {
                _moving = false;
                EndOverPanes(tab);
                if (_groupDrag)
                {
                    _vm?.CommitGroupMove();
                }
                else
                {
                    _vm?.CommitTabMove(tab);
                }
            }

            EndDragVisuals();
            _moving = false;
            _pressed = null;
            _selectOnRelease = false;
            _groupDrag = false;
        };
        Tabs.PreviewMouseDown += OnMouseDown;
        Tabs.MouseRightButtonUp += OnRightUp;

        // Over the whole strip (the arrows too): the wheel up / down scrolls left / right.
        PreviewMouseWheel += (_, e) =>
        {
            if (Scroller.ScrollableWidth > 0)
            {
                ScrollBy(-e.Delta);
                e.Handled = true;
            }
        };
        Scroller.ScrollChanged += (_, _) =>
        {
            _pendingOffset = null;
            UpdateArrows();
            TabsShifted?.Invoke(); // scrolled, or a title got wider or narrower: the tabs moved under the pointer
        };
        LeftArrow.Click += (_, _) => Step(-1);
        RightArrow.Click += (_, _) => Step(1);
        Loaded += (_, _) => AttachWheelHook();
        Unloaded += (_, _) => DetachWheelHook();
    }

    // ---- scrolling: ◀ / ▶, the wheel and the horizontal wheel ----

    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>
    /// Where the last scroll asked to go, until the scroller has applied it (<see cref="ScrollViewer.ScrollToHorizontalOffset"/>
    /// takes effect at the next layout, so wheel messages in between would otherwise start from the old offset and be lost).
    /// </summary>
    private double? _pendingOffset;

    private HwndSource? _hookSource;

    private double CurrentOffset => _pendingOffset ?? Scroller.HorizontalOffset;

    private void ScrollTo(double offset)
    {
        double target = Math.Clamp(offset, 0, Scroller.ScrollableWidth);
        _pendingOffset = target;
        Scroller.ScrollToHorizontalOffset(target);
    }

    private void ScrollBy(double delta) => ScrollTo(CurrentOffset + delta);

    /// <summary>
    /// ◀ / ▶: brings the next tab that is cut off (or hidden) on that side fully into view, aligned
    /// with that edge of the strip, like the sheet tabs of Excel.
    /// </summary>
    private void Step(int direction)
    {
        if (_vm is null)
        {
            return;
        }

        double offset = CurrentOffset;
        double view = Scroller.ViewportWidth;
        if (direction > 0)
        {
            foreach (var tab in _vm.Tabs)
            {
                if (EdgesOf(tab) is { } edges && edges.Right > offset + view + 0.5)
                {
                    ScrollTo(edges.Right - view);
                    return;
                }
            }

            ScrollTo(Scroller.ScrollableWidth);
        }
        else
        {
            for (int i = _vm.Tabs.Count - 1; i >= 0; i--)
            {
                if (EdgesOf(_vm.Tabs[i]) is { } edges && edges.Left < offset - 0.5)
                {
                    ScrollTo(edges.Left);
                    return;
                }
            }

            ScrollTo(0);
        }
    }

    /// <summary>Left and right edge of a tab in the scrolled content's coordinates (null: not realized).</summary>
    private (double Left, double Right)? EdgesOf(TabLayoutViewModel tab)
    {
        if (Tabs.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement container)
        {
            return null;
        }

        double left = container.TranslatePoint(default, Tabs).X;
        return (left, left + container.ActualWidth);
    }

    /// <summary>
    /// Shows ◀ / ▶ only while the tabs do not fit, and greys out the side that is at its end. The
    /// width the arrows take is added back before judging, so showing them never makes the tabs
    /// fit again (and hiding them never makes them overflow): no flip-flop at the boundary.
    /// </summary>
    private void UpdateArrows()
    {
        bool shown = Arrows.Visibility == Visibility.Visible;
        double room = Scroller.ViewportWidth + (shown ? Arrows.ActualWidth + Arrows.Margin.Left + Arrows.Margin.Right : 0);
        bool overflow = Scroller.ExtentWidth > room + 0.5;
        Arrows.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        LeftArrow.IsEnabled = Scroller.HorizontalOffset > 0.5;
        RightArrow.IsEnabled = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 0.5;
        if (overflow && !shown)
        {
            RevealSelected(); // the arrows narrowed the strip: the shown tab may now be cut off on the right
        }
    }

    // WPF has no event for the horizontal wheel (tilt wheel, touchpad): the window procedure gets
    // WM_MOUSEHWHEEL and the strip scrolls when the pointer is over it.
    private void AttachWheelHook()
    {
        if (_hookSource is null && PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnWindowMessage);
            _hookSource = source;
        }
    }

    private void DetachWheelHook()
    {
        if (_hookSource is { } source)
        {
            if (!source.IsDisposed)
            {
                source.RemoveHook(OnWindowMessage);
            }

            _hookSource = null;
        }
    }

    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL || Scroller.ScrollableWidth <= 0 || !IsVisible)
        {
            return 0;
        }

        // lParam: the pointer in screen coordinates (physical pixels); wParam's high word: the
        // distance, positive when tilted to the right (the strip moves to show what is on the right).
        var screen = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        Point p = PointFromScreen(screen);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
        {
            return 0;
        }

        ScrollBy((short)((wParam >> 16) & 0xFFFF));
        handled = true;
        return 1; // TRUE, as browsers do: some mouse drivers keep repeating the tilt only for a window that answers TRUE
    }

    /// <summary>A tab is about to be shown, moved or closed (the main window closes the cascade).</summary>
    public event Action? Interacted;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }

        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged += OnTabsChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            RevealSelected();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RevealSelected();
        TabsShifted?.Invoke(); // a tab was added, closed or moved
    }

    /// <summary>Scrolls the strip so the shown tab is in view (after layout: a new tab has no element yet).</summary>
    private void RevealSelected()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_vm?.SelectedTab is { IsPlaceholder: false } tab && !_moving
                && Tabs.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        });
    }

    // ---- hover: a tab that is not shown is a cascade anchor (MainWindow) ----

    /// <summary>
    /// Where a press (left, middle, right, × too) on the strip left the pointer: until it moves the drag
    /// distance away, nothing opens. Closing or moving a tab slides another one under the still pointer
    /// (WPF raises MouseEnter on layout), and that tab's list must not pop up (as MainWindow._crumbQuiet).
    /// </summary>
    private Point? _quietAt;

    public event Action<FrameworkElement, TabLayoutViewModel>? TabEntered;

    /// <summary>A move with no button held; the position is in the strip's coordinates (they do not scroll with the tabs).</summary>
    public event Action<FrameworkElement, TabLayoutViewModel, Point>? TabMoved;

    public event Action<FrameworkElement>? TabLeft;

    /// <summary>The tabs moved under the pointer without it moving (scrolled; a tab added, closed or moved; a title changed width).</summary>
    public event Action? TabsShifted;

    private bool HoverAllowed(MouseEventArgs e)
    {
        if (_pressed is not null && !_moving && e.LeftButton == MouseButtonState.Released)
        {
            _pressed = null; // released outside the strip: no button up / lost capture reached Tabs
        }

        if (_quietAt is { } at)
        {
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - at.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - at.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return false;
            }

            _quietAt = null;
        }

        return _pressed is null && !_moving
            && e.LeftButton == MouseButtonState.Released
            && e.RightButton == MouseButtonState.Released
            && e.MiddleButton == MouseButtonState.Released;
    }

    private void OnTabEnter(object sender, MouseEventArgs e)
    {
        if (HoverAllowed(e) && sender is FrameworkElement { DataContext: TabLayoutViewModel tab } element)
        {
            TabEntered?.Invoke(element, tab);
        }
    }

    private void OnTabMove(object sender, MouseEventArgs e)
    {
        if (HoverAllowed(e) && sender is FrameworkElement { DataContext: TabLayoutViewModel tab } element)
        {
            TabMoved?.Invoke(element, tab, e.GetPosition(this));
        }
    }

    private void OnTabLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            TabLeft?.Invoke(element);
        }
    }

    private static TabLayoutViewModel? TabAt(object? source) =>
        CardView.FindAncestor<ButtonBase>(source as DependencyObject) is null
            ? (source as FrameworkElement)?.DataContext as TabLayoutViewModel
            : null; // the × button handles its own clicks

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _quietAt = e.GetPosition(this); // before TabAt: a press on × too
        if (TabAt(e.OriginalSource) is not { } tab || _vm is null)
        {
            return;
        }

        e.Handled = true;
        _pressed = tab;
        _pressedAt = e.GetPosition(Tabs);
        _moving = false;
        _overPanes = false;
        Interacted?.Invoke();
        _groupDrag = tab.IsGrouped && OnGroupHandle(e.OriginalSource);
        // A tab that is not on screen is shown on release: what is on screen at the press (a group, or the
        // tab shown alone) stays for a drop on a pane or a snap layout, which lays it out with the dragged tab.
        // A click still fills an active empty pane (on release).
        _selectOnRelease = _groupDrag || (!tab.IsOnScreen && !ReferenceEquals(tab, _vm.SelectedTab));
        if (!_selectOnRelease)
        {
            _vm.SelectTabCommand.Execute(tab);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } tab || _vm is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point p = e.GetPosition(Tabs);
        if (!_moving)
        {
            if (!FileDrag.IsDragGesture(p.X - _pressedAt.X, p.Y - _pressedAt.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)
                || !Tabs.CaptureMouse())
            {
                return;
            }

            _moving = true; // after CaptureMouse: taking the capture resets the press state of a previous one
            _pressed = tab;
            StartGhost(tab);
        }

        _ghost?.MoveTo(Tabs.PointToScreen(p));
        if (_groupDrag)
        {
            MoveGroupTowards(tab, p.X); // along the strip only: a group is not dropped on a pane
            return;
        }

        // Below the strip: over the panes (the window marks the pane it would go in). Back on the strip: reordering again.
        bool overPanes = p.Y > Tabs.ActualHeight + DragOutMargin;
        if (overPanes)
        {
            _overPanes = true;
            bool target = TabDraggedOverPanes?.Invoke(tab, Tabs.PointToScreen(p)) ?? false;
            Tabs.Cursor = target ? Cursors.Hand : Cursors.No;
            return;
        }

        EndOverPanes(tab);
        MoveTowards(tab, p.X);
    }

    /// <summary>The element pressed is (in) the bar over a pane group's tabs.</summary>
    private static bool OnGroupHandle(object? source)
    {
        for (var node = source as DependencyObject; node is not null and not ContentPresenter; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: "GroupHandle" })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The see-through picture of the dragged tab (or of the whole group for a drag by its bar).</summary>
    private void StartGhost(TabLayoutViewModel tab)
    {
        EndDragVisuals();
        var block = _groupDrag && _vm?.StripBlocks().FirstOrDefault(b => b.Contains(tab)) is { } tabs ? tabs : [tab];
        if (EdgesOf(block[0]) is { } first && EdgesOf(block[^1]) is { } last)
        {
            _ghost = DragGhost.Start(Tabs, new Rect(first.Left, 0, Math.Max(0, last.Right - first.Left), Tabs.ActualHeight), _pressedAt);
        }

        if (_groupDrag)
        {
            Tabs.Cursor = Cursors.SizeAll;
        }
    }

    private void EndDragVisuals()
    {
        _ghost?.End();
        _ghost = null;
        if (_groupDrag)
        {
            Tabs.ClearValue(CursorProperty);
        }
    }

    /// <summary>
    /// A group dragged by its bar moves past the block beside it (a tab, or another group as a whole)
    /// once the pointer is past that block's middle, as a single tab does.
    /// </summary>
    private void MoveGroupTowards(TabLayoutViewModel tab, double x)
    {
        if (_vm is null)
        {
            return;
        }

        for (int guard = 0; guard < 64; guard++)
        {
            var blocks = _vm.StripBlocks();
            int at = -1;
            for (int i = 0; i < blocks.Count && at < 0; i++)
            {
                at = blocks[i].Contains(tab) ? i : -1;
            }

            if (at < 0)
            {
                return;
            }

            bool right = at + 1 < blocks.Count && MiddleOf(blocks[at + 1]) is { } r && x > r;
            bool left = !right && at > 0 && MiddleOf(blocks[at - 1]) is { } l && x < l;
            if (!(right || left) || !_vm.MoveGroupBlock(tab, right))
            {
                return;
            }

            Tabs.UpdateLayout(); // the next move is judged against the new positions
        }
    }

    /// <summary>Horizontal middle of a block of tabs, in the strip's coordinates (null: not realized).</summary>
    private double? MiddleOf(IReadOnlyList<TabLayoutViewModel> block) =>
        EdgesOf(block[0]) is { } first && EdgesOf(block[^1]) is { } last ? (first.Left + last.Right) / 2 : null;

    /// <summary>How far below the strip a dragged tab is still over it (DIP).</summary>
    private const double DragOutMargin = 8;

    /// <summary>
    /// The pointer is over the panes while a tab is dragged (in screen coordinates): the window marks
    /// the pane the tab would go in; true when there is one. Null: the pointer left the panes (back on
    /// the strip, or the drag ended): the mark goes.
    /// </summary>
    public event Func<TabLayoutViewModel, Point?, bool>? TabDraggedOverPanes;

    /// <summary>A dragged tab was released over the panes (screen coordinates): true when a pane took it.</summary>
    public event Func<TabLayoutViewModel, Point, bool>? TabDroppedOnPanes;

    private void EndOverPanes(TabLayoutViewModel tab)
    {
        if (_overPanes)
        {
            _overPanes = false;
            Tabs.ClearValue(CursorProperty);
            TabDraggedOverPanes?.Invoke(tab, null);
        }
    }

    /// <summary>
    /// Swaps the dragged tab with a neighbour once the pointer is past that neighbour's middle
    /// (so tabs of different widths do not flip back and forth).
    /// </summary>
    private void MoveTowards(TabLayoutViewModel tab, double x)
    {
        if (_vm is null)
        {
            return;
        }

        var tabs = _vm.Tabs;
        int from = tabs.IndexOf(tab);
        if (from < 0)
        {
            return;
        }

        int to = from;
        while (to + 1 < tabs.Count && MiddleOf(tabs[to + 1]) is { } right && x > right)
        {
            to++;
        }

        if (to == from)
        {
            while (to - 1 >= 0 && MiddleOf(tabs[to - 1]) is { } left && x < left)
            {
                to--;
            }
        }

        if (to != from)
        {
            _vm.MoveTab(tab, to);
            Tabs.UpdateLayout(); // the next move is judged against the new positions
        }
    }

    /// <summary>Horizontal middle of a tab, in the strip's coordinates (null: not realized).</summary>
    private double? MiddleOf(TabLayoutViewModel tab) =>
        Tabs.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container
            ? container.TranslatePoint(new Point(container.ActualWidth / 2, 0), Tabs).X
            : null;

    private void EndPress(MouseButtonEventArgs e)
    {
        bool moving = _moving;
        bool select = _selectOnRelease;
        bool group = _groupDrag;
        var tab = _pressed;
        _moving = false; // before the capture goes: LostMouseCapture must not end the drag a second time
        _pressed = null;
        _selectOnRelease = false;
        EndDragVisuals();
        _groupDrag = false;
        if (tab is null || _vm is null)
        {
            return;
        }

        if (moving && group)
        {
            Tabs.ReleaseMouseCapture();
            _vm.CommitGroupMove();
            RevealSelected();
            return;
        }

        if (!moving)
        {
            if (select)
            {
                _vm.SelectTabCommand.Execute(tab);
            }

            return;
        }

        bool overPanes = _overPanes;
        Point at = Tabs.PointToScreen(e.GetPosition(Tabs));
        Tabs.ReleaseMouseCapture();
        _vm.CommitTabMove(tab); // the moves on the strip before it left it stand
        if (overPanes)
        {
            // The drop first, while the window still shows what the drag was over: ending the drag over
            // the panes first would close the snap bar, and a release on it would then miss its layouts.
            TabDroppedOnPanes?.Invoke(tab, at);
            EndOverPanes(tab);
            return;
        }

        EndOverPanes(tab);

        if (select && _vm.Tabs.Contains(tab))
        {
            _vm.SelectTabCommand.Execute(tab);
        }

        RevealSelected();
    }

    /// <summary>Middle click closes the tab under the pointer.</summary>
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _quietAt = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle && TabAt(e.OriginalSource) is { } tab && _vm is not null)
        {
            e.Handled = true;
            Interacted?.Invoke();
            _vm.CloseTabCommand.Execute(tab);
        }
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        _quietAt = e.GetPosition(this);
        if (TabAt(e.OriginalSource) is not { } tab || _vm is not { } vm || e.OriginalSource is not UIElement target)
        {
            return;
        }

        e.Handled = true;
        Interacted?.Invoke();
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };

        // As Explorer's tab menu: タブの複製 first. A fixed tab at its list cannot be copied (it exists once).
        var duplicate = new MenuItem { Header = "タブを複製", IsEnabled = MainViewModel.CanDuplicate(tab) };
        duplicate.Click += (_, _) => _ = vm.DuplicateTabAsync(tab);
        menu.Items.Add(duplicate);

        // Panes: any tab splits (it is shown first; the new pane is empty); a tab goes into the active
        // pane of the group (the one on screen, else the one shown last); a tab of the group on screen
        // can leave it (the tab stays on the strip).
        menu.Items.Add(new Separator());
        bool canSplit = vm.CanSplitTab(tab);
        var columns = new MenuItem { Header = "左右に分割", IsEnabled = canSplit, InputGestureText = ShortcutCatalog.MainKeysOf(ShortcutId.SplitColumns) };
        columns.Click += (_, _) => _ = vm.SplitTabAsync(tab, SplitAxis.Columns);
        menu.Items.Add(columns);
        var rows = new MenuItem { Header = "上下に分割", IsEnabled = canSplit, InputGestureText = ShortcutCatalog.MainKeysOf(ShortcutId.SplitRows) };
        rows.Click += (_, _) => _ = vm.SplitTabAsync(tab, SplitAxis.Rows);
        menu.Items.Add(rows);
        var putIn = new MenuItem { Header = "選択中のペインに入れる", IsEnabled = vm.CanPutInActivePane(tab) };
        putIn.Click += (_, _) => _ = vm.PutInActivePaneAsync(tab);
        menu.Items.Add(putIn);
        if (tab.IsOnScreen)
        {
            var closePane = new MenuItem { Header = "ペインを閉じる" };
            closePane.Click += (_, _) => vm.ClosePane(tab.ActivePane);
            menu.Items.Add(closePane);
        }

        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "タブを閉じる" };
        close.Click += (_, _) => vm.CloseTabCommand.Execute(tab);
        menu.Items.Add(close);
        var others = new MenuItem { Header = "ほかのタブを閉じる", IsEnabled = vm.Tabs.Count > 1 };
        others.Click += (_, _) => _ = vm.CloseOtherTabsAsync(tab);
        menu.Items.Add(others);
        menu.Items.Add(new Separator());

        // The key acts on the last closed tab whatever tab was right-clicked: its keys belong here.
        var reopen = new MenuItem
        {
            Header = "閉じたタブを開き直す",
            IsEnabled = vm.CanReopenClosedTab,
            InputGestureText = ShortcutCatalog.MainKeysOf(ShortcutId.ReopenTab),
        };
        reopen.Click += (_, _) => vm.ReopenClosedTabCommand.Execute(null);
        menu.Items.Add(reopen);
        menu.IsOpen = true;
    }
}
