using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// The row left-clicked while the right button was held: shown in Explorer when the right
    /// button is released (the row holds the mouse capture until then).
    /// </summary>
    private FrameworkElement? _rockerRow;
    private ReorderDrag? _drag;

    public SidebarView()
    {
        InitializeComponent();
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
            ItemLeft?.Invoke(row);
        }
    }

    private void OnRowLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.RightButton == MouseButtonState.Pressed)
        {
            // Right button held + left click: shown in Explorer once the right button is released.
            _pressed = null;
            if (_rockerRow is null && sender is FrameworkElement row && row.CaptureMouse())
            {
                _rockerRow = row;
            }

            return;
        }

        // A double click must not open twice (§6.8): only the first press counts.
        _pressed = e.ClickCount == 1 ? ItemOf(sender) : null;
        _pressedAt = e.GetPosition(this);
    }

    private void OnRowMove(object sender, MouseEventArgs e) => TryStartDrag(sender, e);

    private void OnRowLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_rockerRow is not null)
        {
            return; // the left click of the gesture: waits for the right button
        }

        var item = ItemOf(sender);
        bool clicked = item is not null && ReferenceEquals(item, _pressed);
        _pressed = null;
        if (clicked)
        {
            ItemClicked?.Invoke(item!);
        }
    }

    private void OnRowRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement row || ItemOf(sender) is not { } item)
        {
            return;
        }

        if (ReferenceEquals(_rockerRow, row))
        {
            _rockerRow = null;
            row.ReleaseMouseCapture();
            ItemExplorerRequested?.Invoke(item);
            return;
        }

        ItemContextMenu?.Invoke(item, row);
    }

    private void OnRowLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_rockerRow, sender))
        {
            _rockerRow = null;
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
