using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>Preview popup content: name, size · date, and a picture, the first lines of the file, or a sheet's first cells.</summary>
public partial class PreviewView : UserControl
{
    /// <summary>Transparent margin around the visible card (room for the shadow), in DIP.</summary>
    public const double ShadowMargin = 16;

    public const double CardWidth = 320;

    /// <summary>Width available to the picture: card width − padding − border.</summary>
    public const double ImageWidth = CardWidth - 20 - 2;

    /// <summary>Sheet cells are cut (with "…") past this width.</summary>
    public const double CellMaxWidth = 90;

    public static readonly DependencyProperty IsOverflowingProperty =
        DependencyProperty.Register(nameof(IsOverflowing), typeof(bool), typeof(PreviewView), new PropertyMetadata(false));

    private bool _pressed;
    private Point _pressedAt;

    public PreviewView()
    {
        InitializeComponent();
        AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => e.Handled = true), handledEventsToo: true);
        Card.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = e.ClickCount == 1; // a double click must not open twice
            _pressedAt = e.GetPosition(Card);
        };
        Card.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed)
            {
                e.Handled = true;
                _pressed = false;
                Clicked?.Invoke(this);
            }
        };
        Card.MouseMove += (_, e) => TryStartDrag(e);
        Card.MouseLeave += (_, e) =>
        {
            TryStartDrag(e); // left the card with the button held: that is a drag too
            _pressed = false;
        };
        Card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ContextMenuRequested?.Invoke(this);
        };

        // The text / table area only clips: no wheel scrolling, and its size changes stay here.
        Document.PreviewMouseWheel += (_, e) => e.Handled = true;
        Document.ScrollChanged += (_, e) =>
        {
            e.Handled = true;
            IsOverflowing = e.ExtentHeight > e.ViewportHeight + 0.5;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PreviewViewModel old)
            {
                old.PropertyChanged -= OnModelPropertyChanged;
            }

            if (e.NewValue is PreviewViewModel model)
            {
                model.PropertyChanged += OnModelPropertyChanged;
                BuildTable(model.Sheet);
            }
        };
    }

    /// <summary>Left click: open the file, like clicking its row.</summary>
    public event Action<PreviewView>? Clicked;

    public event Action<PreviewView>? ContextMenuRequested;

    /// <summary>
    /// The card was pressed and the pointer moved past the system drag distance: drag the previewed
    /// file out to other apps. The press no longer counts as a click.
    /// </summary>
    public event Action<PreviewView>? DragRequested;

    /// <summary>The visible card (without the shadow margin).</summary>
    public FrameworkElement CardElement => Card;

    public double CardMaxHeight
    {
        get => Card.MaxHeight;
        set => Card.MaxHeight = value;
    }

    /// <summary>The text / table is taller than the card shows (the "以下省略" line appears).</summary>
    public bool IsOverflowing
    {
        get => (bool)GetValue(IsOverflowingProperty);
        private set => SetValue(IsOverflowingProperty, value);
    }

    private void TryStartDrag(MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(Card) - _pressedAt;
        if (!FileDrag.IsDragGesture(moved.X, moved.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _pressed = false; // a drag never opens the file on mouse up
        DragRequested?.Invoke(this);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewViewModel.Sheet) && sender is PreviewViewModel model)
        {
            BuildTable(model.Sheet);
        }
    }

    /// <summary>Lays the sheet out as a compact grid: column letters on top, row numbers on the left.</summary>
    private void BuildTable(SheetExcerpt? sheet)
    {
        Table.Children.Clear();
        Table.RowDefinitions.Clear();
        Table.ColumnDefinitions.Clear();
        TableFrame.Visibility = sheet is { IsEmpty: false } ? Visibility.Visible : Visibility.Collapsed;
        if (sheet is not { IsEmpty: false })
        {
            return;
        }

        var lines = (Brush)FindResource("Overlay.Divider");
        var muted = (Brush)FindResource("Overlay.Muted");
        Table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < sheet.ColumnCount; c++)
        {
            Table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 24, MaxWidth = CellMaxWidth });
        }

        for (int r = 0; r <= sheet.Rows.Count; r++)
        {
            Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddCell(0, 0, string.Empty, TextAlignment.Left, header: true);
        for (int c = 0; c < sheet.ColumnCount; c++)
        {
            AddCell(0, c + 1, SheetExcerpt.ColumnName(c), TextAlignment.Center, header: true);
        }

        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            AddCell(r + 1, 0, (r + 1).ToString(CultureInfo.InvariantCulture), TextAlignment.Right, header: true);
            var row = sheet.Rows[r];
            for (int c = 0; c < sheet.ColumnCount; c++)
            {
                AddCell(r + 1, c + 1, row[c].Text, row[c].IsNumber ? TextAlignment.Right : TextAlignment.Left, header: false);
            }
        }

        void AddCell(int row, int column, string text, TextAlignment alignment, bool header)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = header ? 10 : 11,
                TextAlignment = alignment,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            if (header)
            {
                block.Foreground = muted;
            }

            var cell = new Border
            {
                BorderBrush = lines,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(4, 1, 4, 1),
                Background = header ? lines : null,
                Child = block,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            Table.Children.Add(cell);
        }
    }
}
