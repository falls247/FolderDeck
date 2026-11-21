using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Transfer;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>A row of cards (the unit the grid virtualizes).</summary>
public sealed class CardRow(CardViewModel[] cards)
{
    public CardViewModel[] Cards { get; } = cards;
}

/// <summary>Sizes shared by every card of the grid.</summary>
public sealed partial class GridMetrics : ObservableObject
{
    [ObservableProperty]
    private double _cardWidth = 200;

    [ObservableProperty]
    private double _thumbHeight = 266;

    [ObservableProperty]
    private double _iconSize = 80;
}

public partial class CardGrid : UserControl
{
    public const double Gap = 12;

    /// <summary>
    /// Effect the grid reports for a reorder drop. The drag also carries the file drop for other
    /// apps and offers only Copy | Link (<see cref="FileDragOut.AllowedEffects"/>, never Move), so
    /// the grid answers Copy and shows a plain arrow instead of the copy cursor (GiveFeedback).
    /// </summary>
    private const DragDropEffects ReorderEffect = DragDropEffects.Copy;

    /// <summary>Left/right padding of the rows (ItemsPresenter margin) plus the scroll bar.</summary>
    private const double HorizontalChrome = 16 + 4 + 12;

    /// <summary>Height of the band at the top / bottom edge that scrolls while dragging, and the fastest step.</summary>
    private const double AutoScrollZone = 56;
    private const double AutoScrollMaxStep = 24;
    private const double MarkerWidth = 3;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<CardViewModel>), typeof(CardGrid),
        new PropertyMetadata(null, (d, _) => ((CardGrid)d).Rebuild(force: true)));

    public static readonly DependencyProperty CardSizeProperty = DependencyProperty.Register(
        nameof(CardSize), typeof(int), typeof(CardGrid),
        new PropertyMetadata(200, (d, _) => ((CardGrid)d).Rebuild(force: false)));

    private readonly DispatcherTimer _autoScrollTimer;
    private ScrollViewer? _scroll;
    private int _columns;
    private double? _pendingOffset;
    private ReorderDrag? _drag;
    private VirtualizingStackPanel? _rowsPanel;

    public CardGrid()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild(force: false);
        Loaded += (_, _) => AttachScroll();
        PreviewMouseWheel += OnPreviewMouseWheel;
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
        _autoScrollTimer.Tick += (_, _) => AutoScrollStep();
        DragEnter += OnReorderDragOver;
        DragOver += OnReorderDragOver;
        DragLeave += OnReorderDragLeave;
        Drop += OnReorderDrop;
        GiveFeedback += OnReorderGiveFeedback;
    }

    /// <summary>Raised when the grid scrolled (the cascade closes, §9.6 rule 5).</summary>
    public event EventHandler? Scrolled;

    /// <summary>Ctrl + wheel over the grid: +1 grows, -1 shrinks the cards (§6.9).</summary>
    public event Action<int>? CardSizeStepRequested;

    public IReadOnlyList<CardViewModel>? Items
    {
        get => (IReadOnlyList<CardViewModel>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int CardSize
    {
        get => (int)GetValue(CardSizeProperty);
        set => SetValue(CardSizeProperty, value);
    }

    public GridMetrics Metrics { get; } = new();

    public double VerticalOffset => _scroll?.VerticalOffset ?? 0;

    public void ScrollTo(double offset)
    {
        if (_scroll is null)
        {
            _pendingOffset = offset;
            return;
        }

        if (offset <= 0)
        {
            _scroll.ScrollToTop();
            return;
        }

        // Rows may not be measured yet: apply after layout.
        _pendingOffset = offset;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_pendingOffset is { } o)
            {
                _pendingOffset = null;
                _scroll?.ScrollToVerticalOffset(o);
            }
        });
    }

    /// <summary>Thumbnail request size in pixels for the current card width and DPI (§6.4).</summary>
    public int ThumbPixels => ThumbnailService.Bucket(Metrics.CardWidth * DpiScale);

    public int IconPixels => (int)Math.Min(256, Math.Ceiling(Metrics.IconSize * DpiScale / 48.0) * 48);

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void AttachScroll()
    {
        if (_scroll is not null)
        {
            return;
        }

        RowsHost.ApplyTemplate();
        _scroll = RowsHost.Template.FindName("PART_Scroll", RowsHost) as ScrollViewer;
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += (_, e) =>
            {
                // ScrollChanged bubbles: the overlay's own list (inside a card) scrolling must not
                // count as the grid scrolling, or the wheel over the overlay would close it.
                if (ReferenceEquals(e.OriginalSource, _scroll) && e.VerticalChange != 0)
                {
                    Scrolled?.Invoke(this, EventArgs.Empty);
                }

                if (_drag is not null)
                {
                    UpdateDropTarget(); // rows moved under the pointer (auto-scroll)
                }
            };
            if (_pendingOffset is { } o)
            {
                ScrollTo(o);
            }
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        CardSizeStepRequested?.Invoke(e.Delta > 0 ? 1 : -1);
    }

    /// <summary>
    /// §6.1: columns = max(1, floor((width + 12) / (size + 12))); card width =
    /// (width − 12 × (columns − 1)) / columns.
    /// </summary>
    private void Rebuild(bool force)
    {
        double available = Math.Max(0, ActualWidth - HorizontalChrome);
        if (available <= 0)
        {
            available = Math.Max(CardSize, 1);
        }

        int size = Math.Max(1, CardSize);
        int columns = Math.Max(1, (int)Math.Floor((available + Gap) / (size + Gap)));
        double width = Math.Floor((available - (Gap * (columns - 1))) / columns * 100) / 100;
        width = Math.Max(40, width);

        Metrics.CardWidth = width;
        Metrics.ThumbHeight = Math.Round(width * 4 / 3, 2);
        Metrics.IconSize = Math.Clamp(Math.Round(width * 0.4), 48, 96);

        if (!force && columns == _columns)
        {
            return;
        }

        _columns = columns;
        var items = Items;
        if (items is null || items.Count == 0)
        {
            RowsHost.ItemsSource = Array.Empty<CardRow>();
            return;
        }

        var rows = new CardRow[(items.Count + columns - 1) / columns];
        for (int r = 0; r < rows.Length; r++)
        {
            int start = r * columns;
            int count = Math.Min(columns, items.Count - start);
            var cards = new CardViewModel[count];
            for (int i = 0; i < count; i++)
            {
                cards[i] = items[start + i];
            }

            rows[r] = new CardRow(cards);
        }

        RowsHost.ItemsSource = rows;
    }

    // ---- drag & drop reordering (top level) ---------------------------------------------------

    /// <summary>
    /// Drags <paramref name="card"/> to a new position (blocks until the drop, Esc or a drop
    /// elsewhere) and returns the insertion index in <see cref="Items"/> (0…Count, counted with the
    /// card still in place), or null when the drag was cancelled or dropped outside the grid.
    /// <paramref name="doDragDrop"/> runs the drag with the given source (the grid) and data that
    /// carries <see cref="FileDrag.ReorderFormat"/> next to the file drop: released over the grid it
    /// reorders, released over another app (Explorer, PowerPoint…) that app gets the file.
    /// </summary>
    public int? RunReorderDrag(CardViewModel card, Func<DependencyObject, DragDropEffects> doDragDrop)
    {
        if (_drag is not null || Items is not { Count: > 1 } items || !items.Contains(card))
        {
            return null;
        }

        var layer = AdornerLayer.GetAdornerLayer(this);
        var marker = new DropMarkerAdorner(this, TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue);
        layer?.Add(marker);
        var drag = new ReorderDrag(marker);
        _drag = drag;
        card.IsDragged = true;
        try
        {
            // The grid is the drag source: the card's own container may be recycled while auto-scrolling.
            doDragDrop(this);
        }
        finally
        {
            _drag = null;
            _autoScrollTimer.Stop();
            card.IsDragged = false;
            layer?.Remove(marker);
        }

        return drag.DropIndex;
    }

    private void OnReorderDragOver(object sender, DragEventArgs e)
    {
        if (_drag is not { } drag || !e.Data.GetDataPresent(FileDrag.ReorderFormat))
        {
            return; // files from Explorer, or a card dragged inside a folder: the window handles them
        }

        e.Effects = ReorderEffect & e.AllowedEffects;
        e.Handled = true;
        drag.Pointer = e.GetPosition(this);
        drag.Inside = true;
        UpdateDropTarget();
        UpdateAutoScroll();
    }

    private void OnReorderDragLeave(object sender, DragEventArgs e)
    {
        if (_drag is not { } drag || !e.Data.GetDataPresent(FileDrag.ReorderFormat))
        {
            return;
        }

        // Every leave counts as leaving: moving from one element of the grid to another raises
        // DragLeave on the old one and then DragEnter on the new one (OnReorderDragOver), which marks
        // the drag inside again. A pointer that really left the grid (toolbar, another window, Esc)
        // gets no DragEnter here, so the marker stays hidden and the auto-scroll stays stopped.
        drag.Inside = false;
        _autoScrollTimer.Stop();
        UpdateDropTarget();
    }

    private void OnReorderDrop(object sender, DragEventArgs e)
    {
        if (_drag is not { } drag || !e.Data.GetDataPresent(FileDrag.ReorderFormat))
        {
            return;
        }

        e.Handled = true;
        drag.Pointer = e.GetPosition(this);
        drag.Inside = true;
        UpdateDropTarget();
        drag.DropIndex = drag.Target;
        e.Effects = drag.DropIndex is null ? DragDropEffects.None : ReorderEffect & e.AllowedEffects;
    }

    /// <summary>
    /// The grid is the source of a reorder drag: over the grid it shows a plain arrow (the accent
    /// bar tells where the card lands) rather than the copy cursor of the Copy effect it answers.
    /// Elsewhere (other apps) the system cursors apply.
    /// </summary>
    private void OnReorderGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_drag is { Inside: true } && e.Effects != DragDropEffects.None)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.Arrow);
            e.Handled = true;
        }
    }

    private void UpdateDropTarget()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var hit = drag.Inside ? HitTestInsertion(drag.Pointer) : null;
        drag.Target = hit?.Index;
        drag.Marker.Show(hit?.Marker);
    }

    /// <summary>
    /// Where a drop at <paramref name="p"/> (grid coordinates) inserts: the card under the pointer
    /// (left half = before it, right half = after it), the end below the last card. Only realized
    /// row containers are used (rows are virtualized; the dragged card's own row may be gone).
    /// </summary>
    private (int Index, Rect Marker)? HitTestInsertion(Point p)
    {
        var items = Items;
        _rowsPanel ??= FindDescendant<VirtualizingStackPanel>(RowsHost);
        if (items is null || items.Count == 0 || _columns < 1 || _rowsPanel is not { } panel)
        {
            return null;
        }

        int lastRow = (items.Count - 1) / _columns;
        FrameworkElement? hitRow = null;
        FrameworkElement? firstRow = null;
        FrameworkElement? lastRealized = null;
        int hitIndex = -1;
        int firstIndex = int.MaxValue;
        int lastIndex = -1;
        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is not FrameworkElement { IsVisible: true, DataContext: CardRow } row)
            {
                continue;
            }

            int r = RowsHost.ItemContainerGenerator.IndexFromContainer(row);
            if (r < 0)
            {
                continue;
            }

            double top = row.TranslatePoint(default, this).Y;
            if (p.Y >= top && p.Y < top + row.ActualHeight)
            {
                (hitRow, hitIndex) = (row, r);
                break;
            }

            if (r < firstIndex)
            {
                (firstRow, firstIndex) = (row, r);
            }

            if (r > lastIndex)
            {
                (lastRealized, lastIndex) = (row, r);
            }
        }

        bool toEnd = false;
        if (hitRow is null)
        {
            if (lastRealized is not null && lastIndex == lastRow && p.Y >= lastRealized.TranslatePoint(default, this).Y)
            {
                (hitRow, hitIndex, toEnd) = (lastRealized, lastIndex, true); // empty area after the last card
            }
            else if (firstRow is not null && p.Y < firstRow.TranslatePoint(default, this).Y)
            {
                (hitRow, hitIndex) = (firstRow, firstIndex); // top padding
            }
            else if (lastRealized is not null)
            {
                (hitRow, hitIndex) = (lastRealized, lastIndex);
            }
            else
            {
                return null;
            }
        }

        var cards = ((CardRow)hitRow.DataContext).Cards;
        if (cards.Length == 0)
        {
            return null;
        }

        var origin = hitRow.TranslatePoint(default, this);
        double width = Metrics.CardWidth;
        double pitch = width + Gap;
        int col;
        bool after;
        if (toEnd)
        {
            (col, after) = (cards.Length - 1, true);
        }
        else
        {
            double x = p.X - origin.X;
            col = x <= 0 ? 0 : (int)(x / pitch);
            after = x > (col * pitch) + (width / 2);
            if (col >= cards.Length)
            {
                (col, after) = (cards.Length - 1, true);
            }
        }

        int index = Math.Min(items.Count, (hitIndex * _columns) + col + (after ? 1 : 0));
        double cardLeft = origin.X + (col * pitch);
        double markerX = after ? cardLeft + width + (Gap / 2) : cardLeft - (Gap / 2);
        double height = Math.Max(0, hitRow.ActualHeight - Gap); // the row's bottom margin is the gap
        return (index, new Rect(markerX - (MarkerWidth / 2), origin.Y, MarkerWidth, height));
    }

    private void UpdateAutoScroll()
    {
        if (AutoScrollStepSize() == 0)
        {
            _autoScrollTimer.Stop();
        }
        else if (!_autoScrollTimer.IsEnabled)
        {
            _autoScrollTimer.Start();
        }
    }

    /// <summary>Pixels per tick near the top (negative) or bottom (positive) edge; faster closer to the edge.</summary>
    private double AutoScrollStepSize()
    {
        if (_drag is not { Inside: true } drag || _scroll is null || _scroll.ScrollableHeight <= 0)
        {
            return 0;
        }

        double y = TranslatePoint(drag.Pointer, _scroll).Y;
        double height = _scroll.ActualHeight;
        double zone = Math.Min(AutoScrollZone, height / 4);
        if (zone <= 0)
        {
            return 0;
        }

        if (y < zone)
        {
            return -AutoScrollMaxStep * Math.Clamp((zone - y) / zone, 0.15, 1);
        }

        if (y > height - zone)
        {
            return AutoScrollMaxStep * Math.Clamp((y - (height - zone)) / zone, 0.15, 1);
        }

        return 0;
    }

    private void AutoScrollStep()
    {
        double step = AutoScrollStepSize();
        if (step == 0 || _scroll is null)
        {
            _autoScrollTimer.Stop();
            return;
        }

        _scroll.ScrollToVerticalOffset(Math.Clamp(_scroll.VerticalOffset + step, 0, _scroll.ScrollableHeight));
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>State of the running reorder drag.</summary>
    private sealed class ReorderDrag(DropMarkerAdorner marker)
    {
        public DropMarkerAdorner Marker { get; } = marker;

        /// <summary>Last pointer position in grid coordinates.</summary>
        public Point Pointer { get; set; }

        public bool Inside { get; set; }

        /// <summary>Insertion index under the pointer right now.</summary>
        public int? Target { get; set; }

        /// <summary>Set by the drop; null = cancelled.</summary>
        public int? DropIndex { get; set; }
    }

    /// <summary>The accent bar drawn between cards where the dragged card would land.</summary>
    private sealed class DropMarkerAdorner : Adorner
    {
        private readonly Brush _brush;
        private Rect? _rect;

        public DropMarkerAdorner(UIElement adorned, Brush brush)
            : base(adorned)
        {
            _brush = brush;
            IsHitTestVisible = false;
            IsClipEnabled = true;
        }

        public void Show(Rect? rect)
        {
            if (rect == _rect)
            {
                return;
            }

            _rect = rect;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_rect is { } r)
            {
                drawingContext.DrawRoundedRectangle(_brush, null, r, MarkerWidth / 2, MarkerWidth / 2);
            }
        }
    }
}
