using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>Row list of one cascade level. Reports which row the pointer is on (null = blank area).</summary>
public partial class EntryListView : UserControl
{
    private EntryRowViewModel? _hovered;
    private EntryRowViewModel? _pressed;

    public EntryListView()
    {
        InitializeComponent();
        List.AddHandler(MouseMoveEvent, new MouseEventHandler(OnListMouseMove), handledEventsToo: true);
        List.MouseLeave += (_, _) => SetHovered(null, null);
        List.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnLeftDown), handledEventsToo: true);
        List.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnLeftUp), handledEventsToo: true);
        List.AddHandler(PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(OnRightUp), handledEventsToo: true);

        // The list's own scrolling stays here: ScrollChanged bubbles, and the card grid around the
        // overlay would otherwise take it for a grid scroll and close the whole cascade.
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            e.Handled = true;
            if (e.VerticalChange != 0)
            {
                Scrolled?.Invoke(this);
            }
        }));
    }

    /// <summary>The rows scrolled (wheel, scroll bar or keyboard selection).</summary>
    public event Action<EntryListView>? Scrolled;

    /// <summary>The pointer moved onto a row, or onto blank space (row = null).</summary>
    public event Action<EntryListView, EntryRowViewModel?, FrameworkElement?>? RowHovered;

    public event Action<EntryListView, EntryRowViewModel>? RowClicked;

    public event Action<EntryListView, EntryRowViewModel, FrameworkElement>? RowContextMenu;

    public EntryListViewModel? ViewModel => DataContext as EntryListViewModel;

    public EntryRowViewModel? HoveredRow => _hovered;

    /// <summary>Screen element of a row if it is realized.</summary>
    public FrameworkElement? ContainerOf(EntryRowViewModel row) => List.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;

    public void ScrollRowIntoView(EntryRowViewModel row) => List.ScrollIntoView(row);

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        var container = RowContainerAt(e.OriginalSource as DependencyObject);
        SetHovered(container?.DataContext as EntryRowViewModel, container);
    }

    private void SetHovered(EntryRowViewModel? row, FrameworkElement? container)
    {
        if (ReferenceEquals(row, _hovered))
        {
            return;
        }

        _hovered = row;
        RowHovered?.Invoke(this, row, container);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        var container = RowContainerAt(e.OriginalSource as DependencyObject);
        _pressed = e.ClickCount == 1 ? container?.DataContext as EntryRowViewModel : null;
        if (container is not null)
        {
            e.Handled = true; // no ListBox selection / focus changes
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        var container = RowContainerAt(e.OriginalSource as DependencyObject);
        var row = container?.DataContext as EntryRowViewModel;
        if (row is not null && ReferenceEquals(row, _pressed))
        {
            e.Handled = true;
            _pressed = null;
            RowClicked?.Invoke(this, row);
        }

        _pressed = null;
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var container = RowContainerAt(e.OriginalSource as DependencyObject);
        if (container?.DataContext is EntryRowViewModel row)
        {
            e.Handled = true;
            RowContextMenu?.Invoke(this, row, container);
        }
    }

    private static ListBoxItem? RowContainerAt(DependencyObject? source) => CardView.FindAncestor<ListBoxItem>(source);
}
