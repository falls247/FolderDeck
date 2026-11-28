using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderDeck.Core.Input;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The sidebar: the sections クイック アクセス (rows that open 最近の履歴 / よく使う項目), 登録フォルダ,
/// 登録ペイン (saved pane layouts) and PC (the drives), each folded to its header or open. For the
/// 登録フォルダ rows and the drive rows alike it reports hover (the cascade opens beside the row),
/// clicks, right-clicks and the "right button held + left click" gesture; the main window decides
/// what they do. Only a 登録フォルダ row reports a drag. A 登録ペイン row reports its click and its
/// right-click only (<see cref="PaneItemClicked"/>, <see cref="PaneItemContextMenu"/>). The headers
/// and the quick rows are plain buttons bound to the view model.
/// </summary>
public partial class SidebarView : UserControl
{
    private const DragDropEffects ReorderEffect = DragDropEffects.Copy;

    /// <summary>
    /// Share of the sidebar's height the drives may take while the 登録フォルダ rows are shown too
    /// (more drives than that scroll): the registered folders keep most of the column.
    /// </summary>
    private const double PcMaxShare = 0.4;

    /// <summary>
    /// Share of the sidebar's height the 登録ペイン rows may take while another section stretches
    /// (more rows than that scroll): the list is usually short and the folders keep the column.
    /// </summary>
    private const double PanesMaxShare = 0.25;

    private SidebarItemViewModel? _pressed;
    private Point _pressedAt;

    /// <summary>The 登録ペイン row pressed with the left button (a release on it is a click).</summary>
    private RegisteredPaneItemViewModel? _pressedPane;

    /// <summary>
    /// A 登録ペイン row was left-clicked with the right button held: that is no gesture of these rows,
    /// so the release of the right button shows no menu either.
    /// </summary>
    private bool _paneRocker;

    /// <summary>
    /// Right button held + left click on a row: the folder is shown in Explorer when the right
    /// button is released.
    /// </summary>
    private readonly RockerGesture<SidebarItemViewModel> _rocker = new();

    /// <summary>
    /// The row that was just clicked, right-clicked or dragged: moving on it does not ask for its
    /// list again (<see cref="ItemMoved"/>) until the pointer has left it.
    /// </summary>
    private FrameworkElement? _noRearm;
    private ReorderDrag? _drag;

    /// <summary>The view model whose sections are watched (<see cref="ApplyRowHeights"/>).</summary>
    private MainViewModel? _vm;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        AddHandler(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler((_, _) =>
        {
            _rocker.RightDown();
            _paneRocker = false;
        }), handledEventsToo: true);
        MouseLeave += (_, _) =>
        {
            _rocker.Cancel();
            _paneRocker = false;
            _pressedPane = null;
        };
        Scroller.ScrollChanged += OnRowsScrolled;
        PanesScroller.ScrollChanged += OnRowsScrolled;
        PcScroller.ScrollChanged += OnRowsScrolled;
        SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
            {
                ApplyRowHeights(); // the share of the drives follows the height
            }
        };

        // A dragged row lands among the 登録フォルダ rows only: over a header or a quick row the drag
        // is the window's business (which refuses the app's own drags).
        RegisteredBody.DragEnter += OnReorderDragOver;
        RegisteredBody.DragOver += OnReorderDragOver;
        RegisteredBody.DragLeave += OnReorderDragLeave;
        RegisteredBody.Drop += OnReorderDrop;
        GiveFeedback += OnReorderGiveFeedback; // raised on the drag source, which is this control
    }

    /// <summary>The pointer entered a row.</summary>
    public event Action<FrameworkElement, SidebarItemViewModel>? ItemEntered;

    /// <summary>
    /// The pointer moved on a row with no button held (position in this control's coordinates): a
    /// hover that was dropped or closed while the pointer rested there can start again.
    /// </summary>
    public event Action<FrameworkElement, SidebarItemViewModel, Point>? ItemMoved;

    public event Action<FrameworkElement>? ItemLeft;

    public event Action<SidebarItemViewModel>? ItemClicked;

    public event Action<SidebarItemViewModel, FrameworkElement>? ItemContextMenu;

    /// <summary>A row was left-clicked with the right button held, and the right button was then released.</summary>
    public event Action<SidebarItemViewModel>? ItemExplorerRequested;

    /// <summary>The PC section header was right-clicked (its menu: エクスプローラーで開く).</summary>
    public event Action<FrameworkElement>? PcHeaderContextMenu;

    /// <summary>A row was pressed and the pointer moved past the system drag distance.</summary>
    public event Action<SidebarItemViewModel>? ItemDragRequested;

    /// <summary>The rows of a section scrolled: anything anchored to a row is in the wrong place now.</summary>
    public event Action? Scrolled;

    /// <summary>A 登録ペイン row was clicked (one left click: pressed and released on it).</summary>
    public event Action<RegisteredPaneItemViewModel>? PaneItemClicked;

    /// <summary>A 登録ペイン row was right-clicked (its menu: 開く / 現在のペインで上書き / 名前の変更 / 削除), placed at the row.</summary>
    public event Action<RegisteredPaneItemViewModel, FrameworkElement>? PaneItemContextMenu;

    private void OnRowsScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
        {
            Scrolled?.Invoke();
        }
    }

    // ---- sections -----------------------------------------------------------------------------

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.RegisteredSection.PropertyChanged -= OnSectionChanged;
            _vm.PanesSection.PropertyChanged -= OnSectionChanged;
            _vm.PcSection.PropertyChanged -= OnSectionChanged;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.RegisteredSection.PropertyChanged += OnSectionChanged;
            vm.PanesSection.PropertyChanged += OnSectionChanged;
            vm.PcSection.PropertyChanged += OnSectionChanged;
        }

        ApplyRowHeights();
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarSectionViewModel.IsExpanded))
        {
            ApplyRowHeights();
        }
    }

    /// <summary>
    /// Hands the height that is left to one section (its rows scroll inside it): the 登録フォルダ
    /// rows while they are shown; else the drives; else the 登録ペイン rows (a short list, so it
    /// stretches only when it is the one section open). The sections that do not stretch are as
    /// high as their rows need, the 登録ペイン rows up to <see cref="PanesMaxShare"/> of the sidebar
    /// and the drives up to <see cref="PcMaxShare"/>. While all three are folded nothing stretches:
    /// the filler row takes the rest, so the headers stay stacked at the top. (The rows themselves
    /// are hidden by bindings in the XAML.)
    /// </summary>
    private void ApplyRowHeights()
    {
        bool registered = _vm?.RegisteredSection.IsExpanded ?? true;
        bool panes = _vm?.PanesSection.IsExpanded ?? true;
        bool pc = _vm?.PcSection.IsExpanded ?? true;
        bool pcStretches = !registered && pc;
        bool panesStretch = !registered && !pc && panes;
        var star = new GridLength(1, GridUnitType.Star);
        RegisteredBodyRow.Height = registered ? star : GridLength.Auto;
        PanesBodyRow.Height = panesStretch ? star : GridLength.Auto;
        PcBodyRow.Height = pcStretches ? star : GridLength.Auto;
        FillerRow.Height = registered || panes || pc ? new GridLength(0) : star;

        // The limit is on the scroller, not on the row: an Auto row measures its content without a
        // limit, so the scroller would never learn that it has to scroll.
        PanesScroller.MaxHeight = panesStretch ? double.PositiveInfinity : Math.Max(0, ActualHeight * PanesMaxShare);
        PcScroller.MaxHeight = pcStretches ? double.PositiveInfinity : Math.Max(0, ActualHeight * PcMaxShare);
    }

    // ---- pointer ------------------------------------------------------------------------------

    private static SidebarItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as SidebarItemViewModel;

    private void OnRowEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement row && ItemOf(sender) is { } item)
        {
            ItemEntered?.Invoke(row, item);
        }
    }

    private void OnRowLeave(object sender, MouseEventArgs e)
    {
        TryStartDrag(sender, e); // left the row with the button held: that is a drag too
        _pressed = null;
        if (sender is FrameworkElement row)
        {
            if (ReferenceEquals(row, _noRearm))
            {
                _noRearm = null;
            }

            ItemLeft?.Invoke(row);
        }
    }

    private void OnRowLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool rightHeld = e.RightButton == MouseButtonState.Pressed;
        if (_rocker.LeftDown(rightHeld, ItemOf(sender)) || rightHeld)
        {
            // Right button held + left click: shown in Explorer once the right button is released.
            // Neither a click nor the start of a drag.
            _pressed = null;
            return;
        }

        // A double click must not open twice (§6.8): only the first press counts.
        _pressed = e.ClickCount == 1 ? ItemOf(sender) : null;
        _pressedAt = e.GetPosition(this);
    }

    private void OnRowMove(object sender, MouseEventArgs e)
    {
        TryStartDrag(sender, e);
        if (sender is FrameworkElement row
            && !ReferenceEquals(row, _noRearm)
            && e.LeftButton == MouseButtonState.Released
            && e.RightButton == MouseButtonState.Released
            && ItemOf(sender) is { } item)
        {
            ItemMoved?.Invoke(row, item, e.GetPosition(this));
        }
    }

    private void OnRowLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_rocker.LeftUp())
        {
            _pressed = null;
            return; // the left click of the gesture: waits for the right button
        }

        var item = ItemOf(sender);
        bool clicked = item is not null && ReferenceEquals(item, _pressed);
        _pressed = null;
        if (clicked)
        {
            _noRearm = sender as FrameworkElement; // the folder opens in a tab: its list does not come back under the pointer
            ItemClicked?.Invoke(item!);
        }
    }

    private void OnRowRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _noRearm = sender as FrameworkElement; // Explorer or the menu takes over
        if (_rocker.RightUp() is { } armed)
        {
            ItemExplorerRequested?.Invoke(armed); // the row that was clicked, wherever the release lands
            return;
        }

        if (sender is FrameworkElement row && ItemOf(sender) is { } item)
        {
            ItemContextMenu?.Invoke(item, row);
        }
    }

    private void OnPcHeaderRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_rocker.RightUp() is { } armed)
        {
            // A row was left-clicked with the right button held: released here, the row is still shown in Explorer, no menu.
            ItemExplorerRequested?.Invoke(armed);
            return;
        }

        if (sender is FrameworkElement header)
        {
            PcHeaderContextMenu?.Invoke(header);
        }
    }

    // ---- 登録ペイン rows ---------------------------------------------------------------------------

    private static RegisteredPaneItemViewModel? PaneOf(object sender) => (sender as FrameworkElement)?.DataContext as RegisteredPaneItemViewModel;

    private void OnPaneRowLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool rightHeld = e.RightButton == MouseButtonState.Pressed;
        _rocker.LeftDown(rightHeld, null); // never arms on these rows; a press without the right button disarms
        if (rightHeld)
        {
            _paneRocker = true; // neither a click nor (on the right release) the menu
            _pressedPane = null;
            return;
        }

        // A double click must not open twice: only the first press counts.
        _pressedPane = e.ClickCount == 1 ? PaneOf(sender) : null;
    }

    private void OnPaneRowLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var pressed = _pressedPane;
        _pressedPane = null;
        if (_rocker.LeftUp() || _paneRocker)
        {
            return; // the left click of a gesture (begun on a folder row, or here): no click
        }

        if (PaneOf(sender) is { } item && ReferenceEquals(item, pressed))
        {
            PaneItemClicked?.Invoke(item);
        }
    }

    private void OnPaneRowRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pressedPane = null;
        if (_rocker.RightUp() is { } armed)
        {
            // A folder row was left-clicked with the right button held: released here, it is still shown in Explorer, no menu.
            ItemExplorerRequested?.Invoke(armed);
            _paneRocker = false;
            return;
        }

        if (_paneRocker)
        {
            _paneRocker = false;
            return;
        }

        if (sender is FrameworkElement row && PaneOf(sender) is { } item)
        {
            PaneItemContextMenu?.Invoke(item, row);
        }
    }

    private void TryStartDrag(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } item || !ReferenceEquals(item, ItemOf(sender)) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (item.IsDrive)
        {
            return; // a drive is neither moved nor handed to another app: the press stays a click
        }

        Vector moved = e.GetPosition(this) - _pressedAt;
        if (!FileDrag.IsDragGesture(moved.X, moved.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _pressed = null; // a drag never opens the row on mouse up
        _noRearm = sender as FrameworkElement;
        ItemDragRequested?.Invoke(item);
    }

    // ---- drag & drop reordering ---------------------------------------------------------------

    /// <summary>
    /// Drags <paramref name="item"/> to a new position (blocks until the drop, Esc or a drop
    /// elsewhere) and returns the insertion index (0…Count, counted with the row still in place),
    /// or null when the drag was cancelled or dropped outside the 登録フォルダ rows.
    /// <paramref name="doDragDrop"/> runs the drag with the given source (this control, which
    /// outlives the rows) and data that carries <see cref="FileDrag.SidebarReorderFormat"/>.
    /// </summary>
    public int? RunReorderDrag(SidebarItemViewModel item, Func<DependencyObject, DragDropEffects> doDragDrop)
    {
        if (_drag is not null)
        {
            return null;
        }

        var drag = new ReorderDrag();
        _drag = drag;
        item.IsDragged = true;
        try
        {
            doDragDrop(this);
        }
        finally
        {
            _drag = null;
            item.IsDragged = false;
            DropLine.Visibility = Visibility.Collapsed;
        }

        return drag.DropIndex;
    }

    private bool IsReorder(DragEventArgs e) => _drag is not null && e.Data.GetDataPresent(FileDrag.SidebarReorderFormat);

    private void OnReorderDragOver(object sender, DragEventArgs e)
    {
        if (!IsReorder(e))
        {
            return; // folders from Explorer: the window registers them
        }

        e.Effects = ReorderEffect & e.AllowedEffects;
        e.Handled = true;
        _drag!.Inside = true;
        ShowTarget(e.GetPosition(RegisteredBody));
    }

    private void OnReorderDragLeave(object sender, DragEventArgs e)
    {
        if (!IsReorder(e))
        {
            return;
        }

        // Moving from one row to another raises DragLeave, then DragEnter (which shows the line again).
        _drag!.Inside = false;
        _drag.Target = null;
        DropLine.Visibility = Visibility.Collapsed;
    }

    private void OnReorderDrop(object sender, DragEventArgs e)
    {
        if (!IsReorder(e))
        {
            return;
        }

        e.Handled = true;
        ShowTarget(e.GetPosition(RegisteredBody));
        _drag!.DropIndex = _drag.Target;
        e.Effects = _drag.DropIndex is null ? DragDropEffects.None : ReorderEffect & e.AllowedEffects;
    }

    /// <summary>Over the 登録フォルダ rows the line tells where the row lands: a plain arrow, not the copy cursor.</summary>
    private void OnReorderGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_drag is { Inside: true } && e.Effects != DragDropEffects.None)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.Arrow);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The insertion index for a drop at <paramref name="p"/> (coordinates of the 登録フォルダ body,
    /// which is also what the drop line is placed in): before the row whose upper half the pointer
    /// is on or above, else after the last row. The line never leaves the body.
    /// </summary>
    private void ShowTarget(Point p)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        int count = Rows.Items.Count;
        int index = count;
        double lineY = 0;
        for (int i = 0; i < count; i++)
        {
            if (Rows.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            Point top = container.TranslatePoint(new Point(0, 0), RegisteredBody);
            lineY = top.Y + container.ActualHeight;
            if (p.Y < top.Y + (container.ActualHeight / 2))
            {
                index = i;
                lineY = top.Y;
                break;
            }
        }

        drag.Target = count == 0 ? null : index;
        if (drag.Target is null || lineY < 0 || lineY > RegisteredBody.ActualHeight)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }

        DropLine.Margin = new Thickness(8, Math.Max(0, lineY - 1), 8, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private sealed class ReorderDrag
    {
        /// <summary>The pointer is over the 登録フォルダ rows.</summary>
        public bool Inside { get; set; }

        public int? Target { get; set; }

        public int? DropIndex { get; set; }
    }
}
