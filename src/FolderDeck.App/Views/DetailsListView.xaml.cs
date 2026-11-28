using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>Column widths shared by the header and every row of the details list (the rows bind to them).</summary>
public sealed partial class DetailsMetrics : ObservableObject
{
    [ObservableProperty]
    private GridLength _nameWidth = new(DetailsColumns.Defaults[DetailsColumns.Name]);

    [ObservableProperty]
    private GridLength _modifiedWidth = new(DetailsColumns.Defaults[DetailsColumns.Modified]);

    [ObservableProperty]
    private GridLength _typeWidth = new(DetailsColumns.Defaults[DetailsColumns.Type]);

    [ObservableProperty]
    private GridLength _sizeWidth = new(DetailsColumns.Defaults[DetailsColumns.Size]);

    /// <summary>The last column: room for the × of a removable row, otherwise a little padding.</summary>
    [ObservableProperty]
    private GridLength _endWidth = new(DetailsListView.EndPadding);

    /// <summary>One slot per extra column (<see cref="DetailsPropertyColumns.All"/>, in order): its width, 0 while hidden.</summary>
    public IReadOnlyList<DetailsColumnSlot> Extras { get; } = DetailsPropertyColumns.All.Select(c => new DetailsColumnSlot(c.Id)).ToList();
}

/// <summary>An extra column of the details view as drawn: its width (0 while hidden) and whether it is shown (the rows bind their values only then).</summary>
public sealed partial class DetailsColumnSlot(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    private GridLength _width = new(0);

    [ObservableProperty]
    private bool _isShown;
}

/// <summary>
/// The details view of a tab's items: a header (a click sorts, the grips resize) and one row per
/// item (<see cref="DetailsRowView"/>). The other view of the same items is <see cref="CardGrid"/>;
/// the main window shows one of them and gives the hidden one no items.
/// </summary>
public partial class DetailsListView : UserControl, IItemsView
{
    /// <summary>
    /// Height of a row as set (every row has it). It follows 文字の大きさ: the same value
    /// <see cref="TextScale.Apply"/> gives the rows (resource Size.Details.Row). Layout rounding may
    /// make the laid-out height differ slightly, so positions are stepped by a realized row's ActualHeight.
    /// </summary>
    public static double RowHeight => TextScale.DetailsRowHeight;

    /// <summary>Width of the last column when the rows can be removed (the ×), and when they cannot.</summary>
    public const double CloseColumn = 28;
    public const double EndPadding = 8;

    /// <summary>
    /// Left and right padding of the rows (ItemsPresenter margin) plus the scroll bar (the width
    /// the app's ScrollBar style gives it). The header's margin in XAML mirrors the same numbers.
    /// </summary>
    private const double HorizontalChrome = 12 + 2 + 10;

    /// <summary>Height of the band at the top / bottom edge that scrolls while dragging, and the fastest step.</summary>
    private const double AutoScrollZone = 40;
    private const double AutoScrollMaxStep = 24;
    private const double MarkerHeight = 3;

    // Segoe Fluent Icons: chevron up (ascending) and chevron down (descending).
    private const string AscendingGlyph = "\uE70E";
    private const string DescendingGlyph = "\uE70D";

    /// <summary>Effect answered for a reorder drop: see <see cref="CardGrid"/>'s constant of the same name.</summary>
    private const DragDropEffects ReorderEffect = DragDropEffects.Copy;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<CardViewModel>), typeof(DetailsListView),
        new PropertyMetadata(null, (d, _) => ((DetailsListView)d).OnItemsChanged()));

    public static readonly DependencyProperty SortModeProperty = DependencyProperty.Register(
        nameof(SortMode), typeof(CardSortMode), typeof(DetailsListView),
        new PropertyMetadata(CardSortMode.Manual, (d, _) => ((DetailsListView)d).UpdateSortMarks()));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(IReadOnlyDictionary<string, double>), typeof(DetailsListView),
        new PropertyMetadata(null, (d, _) => ((DetailsListView)d).UpdateColumns()));

    public static readonly DependencyProperty ExtraColumnsProperty = DependencyProperty.Register(
        nameof(ExtraColumns), typeof(IReadOnlyList<string>), typeof(DetailsListView),
        new PropertyMetadata(null, (d, _) => ((DetailsListView)d).UpdateColumns()));

    /// <summary>The extra columns shown (ids of <see cref="DetailsPropertyColumns"/>), after サイズ.</summary>
    public IReadOnlyList<string>? ExtraColumns
    {
        get => (IReadOnlyList<string>?)GetValue(ExtraColumnsProperty);
        set => SetValue(ExtraColumnsProperty, value);
    }

    /// <summary>The header was right-clicked: the column chooser (the element to open it at).</summary>
    public event Action<FrameworkElement>? HeaderMenuRequested;

    /// <summary>The extra columns' titles and grips in the header (index = <see cref="DetailsPropertyColumns.All"/>).</summary>
    private readonly List<(ColumnDefinition Column, Button Title, TextBlock Text, Thumb Grip)> _extraHeaders = [];

    /// <summary>
    /// The extra columns' header cells, after サイズ (before the empty * column): a title that does not
    /// sort (the values arrive late) and a grip; collapsed and 0 wide while the column is hidden.
    /// </summary>
    private void BuildExtraHeaders()
    {
        var cellStyle = (Style)Resources["HeaderCell"];
        var gripStyle = (Style)Resources["HeaderGrip"];
        var columns = DetailsPropertyColumns.All;
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var definition = new ColumnDefinition { Width = new GridLength(0) };
            HeaderGrid.ColumnDefinitions.Insert(DetailsRowView.FirstExtraColumn + i, definition);
            var text = new TextBlock { Text = column.Title };
            var title = new Button
            {
                Style = cellStyle,
                Content = text,
                Cursor = Cursors.Arrow,
                ToolTip = "右クリックで表示する列を選択（この列では並べ替えできません）",
                HorizontalContentAlignment = column.AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
            };
            System.Windows.Automation.AutomationProperties.SetName(title, column.Title);
            Grid.SetColumn(title, DetailsRowView.FirstExtraColumn + i);
            HeaderGrid.Children.Add(title);
            var grip = new Thumb { Style = gripStyle, Tag = column.Id, Visibility = Visibility.Collapsed };
            grip.DragStarted += OnGripDragStarted;
            grip.DragDelta += OnGripDragDelta;
            grip.DragCompleted += OnGripDragCompleted;
            Grid.SetColumn(grip, DetailsRowView.FirstExtraColumn + i);
            HeaderGrid.Children.Add(grip);
            _extraHeaders.Add((definition, title, text, grip));
        }

        // Every grip above every title: a grip reaches 4 px into the column on its right (its margin),
        // and the titles added here after the grips of the XAML would otherwise cover that half.
        foreach (var grip in HeaderGrid.Children.OfType<Thumb>())
        {
            Panel.SetZIndex(grip, 1);
        }
    }

    /// <summary>Windows' own names of the extra columns (as Explorer's headers), once they are read.</summary>
    public void SetExtraTitles(IReadOnlyDictionary<string, string> titles)
    {
        var columns = DetailsPropertyColumns.All;
        for (int i = 0; i < columns.Count && i < _extraHeaders.Count; i++)
        {
            if (titles.TryGetValue(columns[i].Id, out string? title) && title.Length > 0)
            {
                _extraHeaders[i].Text.Text = title;
                System.Windows.Automation.AutomationProperties.SetName(_extraHeaders[i].Title, title);
            }
        }
    }

    private void OnHeaderRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // not the blank area's menu (MainWindow.OnContentRightUp)
        HeaderMenuRequested?.Invoke(HeaderBar);
    }

    private readonly DispatcherTimer _autoScrollTimer;
    private ScrollViewer? _scroll;
    private double? _pendingOffset;
    private VirtualizingStackPanel? _rowsPanel;
    private ReorderDrag? _drag;
    private ColumnResize? _resize;

    public DetailsListView()
    {
        InitializeComponent();
        BuildExtraHeaders();
        SizeChanged += (_, _) => UpdateColumns();

        // While collapsed (card modes) the width reads 0, and the items arrive before the list is
        // shown; shown at the size it had before, no SizeChanged follows.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                UpdateColumns();
            }
            else
            {
                _marquee?.End(); // switched to a card view
            }
        };
        Loaded += (_, _) => AttachScroll();
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
        _autoScrollTimer.Tick += (_, _) => AutoScrollStep();
        DragEnter += OnReorderDragOver;
        DragOver += OnReorderDragOver;
        DragLeave += OnReorderDragLeave;
        Drop += OnReorderDrop;
        GiveFeedback += OnReorderGiveFeedback;
        UpdateSortMarks();
    }

    /// <summary>Raised when the list scrolled (a list beside a row no longer sits beside it).</summary>
    public event EventHandler? Scrolled;

    /// <summary>A column title was clicked.</summary>
    public event Action<SortColumn>? HeaderClicked;

    /// <summary>A column was resized (the <see cref="DetailsColumns"/> key and the new width), once, when the grip is released.</summary>
    public event Action<string, double>? ColumnResized;

    /// <summary>The items to show, or null while the list is hidden (then it holds no rows).</summary>
    public IReadOnlyList<CardViewModel>? Items
    {
        get => (IReadOnlyList<CardViewModel>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>The sort mode of the shown tab: the title of its column carries the direction mark.</summary>
    public CardSortMode SortMode
    {
        get => (CardSortMode)GetValue(SortModeProperty);
        set => SetValue(SortModeProperty, value);
    }

    /// <summary>
    /// The stored widths of the columns, 名前 included (<see cref="DetailsColumns"/> keys). They are
    /// drawn through <see cref="DetailsColumns.Fit"/>, so a narrow window shows them smaller
    /// without changing what is stored.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Columns
    {
        get => (IReadOnlyDictionary<string, double>?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public DetailsMetrics Metrics { get; } = new();

    public double VerticalOffset => _scroll?.VerticalOffset ?? 0;

    public FrameworkElement? RealizedItemAt(MouseEventArgs e) => ItemsViewHitTest.Find(_scroll, RowsHost, e);

    /// <summary>One row per item: the item's index is the row the panel brings into view.</summary>
    public FrameworkElement? BringItemIntoView(CardViewModel item)
    {
        if (Items is not { } items || !IsVisible)
        {
            return null;
        }

        int index = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                index = i;
                break;
            }
        }

        _rowsPanel ??= FindDescendant<VirtualizingStackPanel>(RowsHost);
        if (_rowsPanel is null && index >= 0)
        {
            RowsHost.UpdateLayout(); // the items panel exists after the first measure
            _rowsPanel = FindDescendant<VirtualizingStackPanel>(RowsHost);
        }

        return ItemsViewReveal.Reveal(RowsHost, _rowsPanel, index, item);
    }

    /// <summary>One item per row: ↑ ↓ step one item, as ← → do.</summary>
    public int ItemsPerRow => 1;

    public int PageRows
    {
        get
        {
            _rowsPanel ??= FindDescendant<VirtualizingStackPanel>(RowsHost);
            return ItemsViewPaging.PageRows(_scroll, _rowsPanel, RowHeight);
        }
    }

    public void ScrollTo(double offset)
    {
        if (_scroll is null)
        {
            _pendingOffset = offset;
            return;
        }

        if (offset <= 0)
        {
            _pendingOffset = null;
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

    private void OnItemsChanged()
    {
        _marquee?.End(); // another list: the box's rows mean nothing any more
        RowsHost.ItemsSource = Items;
        UpdateColumns(); // the × column is there only for removable rows
    }

    // ---- marquee (rubber-band) selection -------------------------------------------------------

    private MarqueeSelector? _marquee;

    public bool IsMarqueeRunning => _marquee is not null;

    public bool BeginMarquee(MouseButtonEventArgs e, MarqueeHandlers handlers)
    {
        AttachScroll();
        if (_marquee is not null || _drag is not null || _scroll is null || !IsVisible || Items is not { } items)
        {
            return false;
        }

        var marquee = MarqueeSelector.TryStart(_scroll, RowsHost, items, MeasureMarquee, e, handlers);
        if (marquee is null)
        {
            return false;
        }

        _marquee = marquee;
        marquee.Finished += () =>
        {
            if (ReferenceEquals(_marquee, marquee))
            {
                _marquee = null;
            }
        };
        return true;
    }

    public void CancelMarquee() => _marquee?.Cancel();

    /// <summary>
    /// The rows now, from a realized row: one column whose cell is the whole row, the pitch is the
    /// row's laid-out height (not <see cref="RowHeight"/>: see <see cref="HitTestInsertion"/>).
    /// </summary>
    private MarqueeLayout? MeasureMarquee()
    {
        _rowsPanel ??= FindDescendant<VirtualizingStackPanel>(RowsHost);
        if (Items is not { Count: > 0 } items || _rowsPanel is not { } panel)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is FrameworkElement { IsVisible: true, ActualHeight: > 0, ActualWidth: > 0 } row
                && RowsHost.ItemContainerGenerator.IndexFromContainer(row) is int at and >= 0)
            {
                double pitch = row.ActualHeight;
                Point o = row.TranslatePoint(default, RowsHost);
                return new MarqueeLayout(
                    new Point(o.X, o.Y - (at * pitch)),
                    new CellGrid(1, items.Count, row.ActualWidth, pitch, row.ActualWidth, pitch));
            }
        }

        return null;
    }

    // ---- columns: widths, sort marks, resizing ------------------------------------------------

    /// <summary>
    /// Applies the column widths to the header and, through <see cref="Metrics"/>, to the rows:
    /// the stored widths (or the ones of the running resize), fitted to the width of the list.
    /// </summary>
    private void UpdateColumns()
    {
        // Every item of a list comes from the same place, so the first one tells whether rows have a ×.
        double end = Items is { Count: > 0 } items && items[0].CanRemove ? CloseColumn : EndPadding;
        var widths = _resize?.Widths ?? Columns ?? DetailsColumns.Defaults;
        var extras = ExtraColumns ?? [];
        double available = ActualWidth - HorizontalChrome - end;
        var shown = available > 0
            ? DetailsColumns.Fit(available, widths, extras)
            : DetailsColumns.Normalize(widths); // not laid out yet

        var columns = DetailsPropertyColumns.All;
        for (int i = 0; i < columns.Count && i < _extraHeaders.Count; i++)
        {
            bool on = extras.Contains(columns[i].Id);
            var width = new GridLength(on ? shown[columns[i].Id] : 0);
            var slot = Metrics.Extras[i];
            slot.Width = _extraHeaders[i].Column.Width = width;
            slot.IsShown = on;
            _extraHeaders[i].Title.Visibility = _extraHeaders[i].Grip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        Metrics.NameWidth = HeaderName.Width = new GridLength(shown[DetailsColumns.Name]);
        Metrics.ModifiedWidth = HeaderModified.Width = new GridLength(shown[DetailsColumns.Modified]);
        Metrics.TypeWidth = HeaderType.Width = new GridLength(shown[DetailsColumns.Type]);
        Metrics.SizeWidth = HeaderSize.Width = new GridLength(shown[DetailsColumns.Size]);
        Metrics.EndWidth = HeaderEnd.Width = new GridLength(end);
    }

    /// <summary>Marks the title of the sorted column with its direction (none in the manual order).</summary>
    private void UpdateSortMarks()
    {
        var mode = SortMode;
        SortColumn? sorted = CardSortModes.ColumnOf(mode);
        string glyph = CardSortModes.IsDescending(mode) ? DescendingGlyph : AscendingGlyph;
        Mark(NameHeader, NameArrow, SortColumn.Name);
        Mark(ModifiedHeader, ModifiedArrow, SortColumn.Modified);
        Mark(TypeHeader, TypeArrow, SortColumn.Type);
        Mark(SizeHeader, SizeArrow, SortColumn.Size);

        void Mark(Button title, TextBlock arrow, SortColumn column)
        {
            bool on = sorted == column;
            arrow.Text = glyph;
            arrow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            title.SetResourceReference(ForegroundProperty, on ? "Brush.Text" : "Brush.Muted");
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        SortColumn column =
            ReferenceEquals(sender, ModifiedHeader) ? SortColumn.Modified
            : ReferenceEquals(sender, TypeHeader) ? SortColumn.Type
            : ReferenceEquals(sender, SizeHeader) ? SortColumn.Size
            : SortColumn.Name;
        HeaderClicked?.Invoke(column);
    }

    /// <summary>
    /// A grip is dragged. Each grip sits on the right edge of a column and resizes that column (the
    /// one on its left), as in Explorer: the columns to its left do not move, so the grip follows the
    /// pointer. Only the dragged column and the empty space right of the last column change; the
    /// column grows at most into that space (when there is none, another column such as 名前 has to
    /// be narrowed first). The new width is measured from the width as shown and the pointer
    /// against the list (which does not move), and is reported once at the end.
    /// While dragging, the list is drawn exactly as it will be once the width is stored: the stored
    /// widths with only the dragged one replaced, through <see cref="DetailsColumns.Fit"/>. So when
    /// the window is too narrow for the stored widths (名前 shown narrower than stored), narrowing
    /// another column gives the space back to 名前 first and the grip stays put until 名前 is back to
    /// its stored width; the other stored widths are never changed by a drag.
    /// </summary>
    private void OnGripDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: string key })
        {
            return;
        }

        var shown = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [DetailsColumns.Name] = Metrics.NameWidth.Value,
            [DetailsColumns.Modified] = Metrics.ModifiedWidth.Value,
            [DetailsColumns.Type] = Metrics.TypeWidth.Value,
            [DetailsColumns.Size] = Metrics.SizeWidth.Value,
        };
        foreach (var slot in Metrics.Extras.Where(s => s.IsShown))
        {
            shown[slot.Id] = slot.Width.Value; // the shown extra columns take room too
        }

        if (shown.TryGetValue(key, out double start))
        {
            double filler = Math.Max(0, ActualWidth - HorizontalChrome - Metrics.EndWidth.Value - shown.Values.Sum());
            double maxWidth = Math.Min(DetailsColumns.MaxOf(key), Math.Max(start, Math.Floor(start + filler)));
            var stored = DetailsColumns.Normalize(Columns ?? DetailsColumns.Defaults);
            _resize = new ColumnResize(key, start, Mouse.GetPosition(this).X, maxWidth, stored[key], stored);
        }
    }

    private void OnGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_resize is not { } resize)
        {
            return;
        }

        // Math.Max keeps Clamp's bounds ordered; the outer Min keeps the column inside the empty space.
        double min = DetailsColumns.MinOf(resize.Key);
        double width = resize.StartWidth + (Mouse.GetPosition(this).X - resize.StartX);
        resize.Widths[resize.Key] = Math.Min(
            resize.MaxWidth, Math.Round(Math.Clamp(width, min, Math.Max(min, resize.MaxWidth))));
        UpdateColumns();
    }

    private void OnGripDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_resize is not { } resize)
        {
            return;
        }

        _resize = null;
        if (!e.Canceled && resize.Widths[resize.Key] != resize.StoredWidth)
        {
            ColumnResized?.Invoke(resize.Key, resize.Widths[resize.Key]);
        }

        UpdateColumns(); // back to the stored widths (the new one included, once it is stored)
    }

    // ---- drag & drop reordering (top level) ---------------------------------------------------

    /// <summary>
    /// Drags <paramref name="card"/> to a new position (blocks until the drop, Esc or a drop
    /// elsewhere) and returns the insertion index in <see cref="Items"/> (0…Count, counted with the
    /// row still in place), or null when the drag was cancelled or dropped outside the list. A line
    /// between two rows shows where the row would land. The list is the drag source: the row's own
    /// container may be recycled while auto-scrolling.
    /// </summary>
    public int? RunReorderDrag(CardViewModel card, Func<DependencyObject, DragDropEffects> doDragDrop)
    {
        if (_drag is not null || Items is not { Count: > 1 } items || !items.Contains(card))
        {
            return null;
        }

        var layer = AdornerLayer.GetAdornerLayer(this);
        var marker = new DropLineAdorner(this, TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue);
        layer?.Add(marker);
        var drag = new ReorderDrag(marker);
        _drag = drag;
        card.IsDragged = true;
        try
        {
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
            return; // files from Explorer, or a row dragged inside a folder: the window handles them
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

        // Every leave counts as leaving; moving on to another element of the list raises DragEnter
        // there right after, which marks the drag inside again (as in the card grid).
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

    /// <summary>Over the list a reorder drag shows a plain arrow (the line tells where the row lands), not the copy cursor.</summary>
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
    /// Where a drop at <paramref name="p"/> (list coordinates) inserts: at the row boundary nearest
    /// to the pointer (above the first row = 0, below the last = Count). Every row is as high as
    /// any realized one, so one realized row is enough to know where all of them are
    /// (rows are virtualized: the one under the pointer is realized, the dragged one may be gone).
    /// </summary>
    private (int Index, Rect Marker)? HitTestInsertion(Point p)
    {
        var items = Items;
        _rowsPanel ??= FindDescendant<VirtualizingStackPanel>(RowsHost);
        if (items is null || items.Count == 0 || _rowsPanel is not { IsVisible: true } panel)
        {
            return null;
        }

        // Where the first row starts (above the view once the list is scrolled), from any row in use.
        // The pitch is that row's laid-out height, not RowHeight: with layout rounding a row of, say,
        // 30 DIP is 30.4 DIP on a 125 % monitor, and the difference adds up over the rows between
        // the realized one and the pointer (every row has the same Height, so they all round alike).
        double rowHeight = RowHeight;
        double? firstTop = null;
        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int i = 0; i < count && firstTop is null; i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is FrameworkElement { IsVisible: true } row
                && RowsHost.ItemContainerGenerator.IndexFromContainer(row) is int at and >= 0)
            {
                if (row.ActualHeight > 0)
                {
                    rowHeight = row.ActualHeight;
                }

                firstTop = row.TranslatePoint(default, this).Y - (at * rowHeight);
            }
        }

        if (firstTop is not { } first)
        {
            return null;
        }

        Point origin = panel.TranslatePoint(default, this);
        int index = (int)Math.Clamp(Math.Round((p.Y - first) / rowHeight), 0, items.Count);
        double lineY = first + (index * rowHeight);
        double top = RowsHost.TranslatePoint(default, this).Y; // the part of the list that shows rows
        double bottom = top + RowsHost.ActualHeight;
        if (lineY < top - 1 || lineY > bottom + 1)
        {
            return null; // the boundary is scrolled out of view (the pointer is in the header or the padding)
        }

        lineY = Math.Clamp(lineY, top + (MarkerHeight / 2), Math.Max(top + (MarkerHeight / 2), bottom - (MarkerHeight / 2)));
        return (index, new Rect(origin.X, lineY - (MarkerHeight / 2), panel.ActualWidth, MarkerHeight));
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

    /// <summary>Pixels per tick near the top (negative) or bottom (positive) edge of the rows; faster closer to the edge.</summary>
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

    /// <summary>State of the running column resize.</summary>
    private sealed class ColumnResize(
        string key, double startWidth, double startX, double maxWidth, double storedWidth,
        Dictionary<string, double> widths)
    {
        public string Key { get; } = key;

        public double StartWidth { get; } = startWidth;

        /// <summary>Pointer position (list coordinates) when the grip was pressed.</summary>
        public double StartX { get; } = startX;

        /// <summary>The widest the column may get: its start width plus the empty space right of the last column.</summary>
        public double MaxWidth { get; } = maxWidth;

        /// <summary>The stored width of the dragged column when the drag began (an unmoved grip reports nothing).</summary>
        public double StoredWidth { get; } = storedWidth;

        /// <summary>
        /// The stored widths (normalized) when the drag began, with the dragged column's changing; drawn
        /// through <see cref="DetailsColumns.Fit"/> like the stored ones, so the preview matches the result.
        /// </summary>
        public Dictionary<string, double> Widths { get; } = widths;
    }

    /// <summary>State of the running reorder drag.</summary>
    private sealed class ReorderDrag(DropLineAdorner marker)
    {
        public DropLineAdorner Marker { get; } = marker;

        /// <summary>Last pointer position in list coordinates.</summary>
        public Point Pointer { get; set; }

        public bool Inside { get; set; }

        /// <summary>Insertion index under the pointer right now.</summary>
        public int? Target { get; set; }

        /// <summary>Set by the drop; null = cancelled.</summary>
        public int? DropIndex { get; set; }
    }

    /// <summary>The accent line drawn between two rows where the dragged row would land.</summary>
    private sealed class DropLineAdorner : Adorner
    {
        private readonly Brush _brush;
        private Rect? _rect;

        public DropLineAdorner(UIElement adorned, Brush brush)
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
                drawingContext.DrawRoundedRectangle(_brush, null, r, MarkerHeight / 2, MarkerHeight / 2);
            }
        }
    }
}
