using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderDeck.Core.Input;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The 登録フォルダ rows of the sidebar. Reports hover (the cascade opens beside the row), clicks,
/// right-clicks, the "right button held + left click" gesture and drags; the main window decides
/// what they do.
/// </summary>
public partial class SidebarView : UserControl
{
    private const DragDropEffects ReorderEffect = DragDropEffects.Copy;

    private SidebarItemViewModel? _pressed;
    private Point _pressedAt;

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

    public SidebarView()
    {
        InitializeComponent();
        AddHandler(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler((_, _) => _rocker.RightDown()), handledEventsToo: true);
        MouseLeave += (_, _) => _rocker.Cancel();
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0)
            {
                Scrolled?.Invoke();
            }
        };
        DragEnter += OnReorderDragOver;
        DragOver += OnReorderDragOver;
        DragLeave += OnReorderDragLeave;
        Drop += OnReorderDrop;
        GiveFeedback += OnReorderGiveFeedback;
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

    /// <summary>A row was pressed and the pointer moved past the system drag distance.</summary>
    public event Action<SidebarItemViewModel>? ItemDragRequested;

    /// <summary>The rows scrolled: anything anchored to a row is in the wrong place now.</summary>
    public event Action? Scrolled;

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

    private void TryStartDrag(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } item || !ReferenceEquals(item, ItemOf(sender)) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
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
    /// or null when the drag was cancelled or dropped outside the sidebar.
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
        ShowTarget(e.GetPosition(this));
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
        ShowTarget(e.GetPosition(this));
        _drag!.DropIndex = _drag.Target;
        e.Effects = _drag.DropIndex is null ? DragDropEffects.None : ReorderEffect & e.AllowedEffects;
    }

    /// <summary>Over the sidebar the line tells where the row lands: a plain arrow, not the copy cursor.</summary>
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
    /// The insertion index for a drop at <paramref name="p"/> (this control's coordinates): before
    /// the row whose upper half the pointer is on or above, else after the last row.
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

            Point top = container.TranslatePoint(new Point(0, 0), this);
            lineY = top.Y + container.ActualHeight;
            if (p.Y < top.Y + (container.ActualHeight / 2))
            {
                index = i;
                lineY = top.Y;
                break;
            }
        }

        drag.Target = count == 0 ? null : index;
        if (drag.Target is null || lineY < 0 || lineY > ActualHeight)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }

        DropLine.Margin = new Thickness(8, Math.Max(0, lineY - 1), 8, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private sealed class ReorderDrag
    {
        /// <summary>The pointer is over the sidebar.</summary>
        public bool Inside { get; set; }

        public int? Target { get; set; }

        public int? DropIndex { get; set; }
    }
}
