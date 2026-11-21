using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>Left/right padding of the rows (ItemsPresenter margin) plus the scroll bar.</summary>
    private const double HorizontalChrome = 16 + 4 + 12;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<CardViewModel>), typeof(CardGrid),
        new PropertyMetadata(null, (d, _) => ((CardGrid)d).Rebuild(force: true)));

    public static readonly DependencyProperty CardSizeProperty = DependencyProperty.Register(
        nameof(CardSize), typeof(int), typeof(CardGrid),
        new PropertyMetadata(200, (d, _) => ((CardGrid)d).Rebuild(force: false)));

    private ScrollViewer? _scroll;
    private int _columns;
    private double? _pendingOffset;

    public CardGrid()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild(force: false);
        Loaded += (_, _) => AttachScroll();
        PreviewMouseWheel += OnPreviewMouseWheel;
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
                if (e.VerticalChange != 0)
                {
                    Scrolled?.Invoke(this, EventArgs.Empty);
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
}
