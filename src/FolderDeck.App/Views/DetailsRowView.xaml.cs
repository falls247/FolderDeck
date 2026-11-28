using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FolderDeck.Core.Layout;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// One row of the details list: the same item a card shows (<see cref="CardViewModel"/>), drawn
/// as name, modified time, type and size. Clicks, the drag threshold and the right-button gesture
/// are read by the same <see cref="ItemPointerTracker"/> as on a card and go to the same host
/// (<see cref="CardView.Host"/>).
/// <para>
/// Unlike a card the row never realizes its item (<see cref="CardViewModel.Realize"/>): no
/// thumbnail, no cover search, no child counts. That is what keeps a list of thousands of rows
/// cheap, so nothing here may start that loading.
/// </para>
/// </summary>
public partial class DetailsRowView : UserControl
{
    private readonly ItemPointerTracker _pointer;
    private CardViewModel? _vm;

    /// <summary>The item the row shows (its DataContext), or null before it is bound.</summary>
    public CardViewModel? ViewModel => _vm;

    /// <summary>
    /// A button went down on the row (a click, a menu, a drag): moving on the name does not
    /// ask for the folder's list again until the pointer has left the name (as on a sidebar row).
    /// </summary>
    private bool _noRearm;

    /// <summary>One automation element with the item's name, nothing inside (<see cref="ItemViewAutomationPeer"/>).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ItemViewAutomationPeer(this);

    public DetailsRowView()
    {
        InitializeComponent();
        BuildExtraCells();
        _pointer = new ItemPointerTracker(this, () => _vm);

        // Tunnelling, handled or not: every press on the row counts, also one on its buttons.
        AddHandler(
            PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler((_, _) =>
            {
                _pointer.RightDown();
                _noRearm = true;
            }),
            handledEventsToo: true);
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _noRearm = true), handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Where the extra columns go in the row's grid: after 名前 / 更新日時 / 種類 / サイズ.</summary>
    internal const int FirstExtraColumn = 4;

    /// <summary>
    /// The extra columns (<see cref="DetailsPropertyColumns"/>) after サイズ: a column per entry whose
    /// width follows <see cref="DetailsMetrics.Extras"/> (0 while hidden) and a cell whose text is
    /// bound to <c>Props[id]</c> only while the column is shown (a trigger sets the binding: a
    /// collapsed cell's binding would still be read and open the file's property store). The ×
    /// column and the selection frame move right by as many columns.
    /// </summary>
    private void BuildExtraCells()
    {
        var columns = DetailsPropertyColumns.All;
        foreach (UIElement child in RowGrid.Children)
        {
            int column = Grid.GetColumn(child);
            if (column >= FirstExtraColumn)
            {
                Grid.SetColumn(child, column + columns.Count);
            }
            else if (Grid.GetColumnSpan(child) > FirstExtraColumn)
            {
                Grid.SetColumnSpan(child, Grid.GetColumnSpan(child) + columns.Count);
            }
        }

        var listView = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DetailsListView), 1);
        var cellStyle = (Style)Resources["Cell"];
        for (int i = 0; i < columns.Count; i++)
        {
            var definition = new ColumnDefinition();
            BindingOperations.SetBinding(definition, ColumnDefinition.WidthProperty, new Binding($"Metrics.Extras[{i}].Width") { RelativeSource = listView });
            RowGrid.ColumnDefinitions.Insert(FirstExtraColumn + i, definition);

            var shown = new DataTrigger { Binding = new Binding($"Metrics.Extras[{i}].IsShown") { RelativeSource = listView }, Value = true };
            shown.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
            shown.Setters.Add(new Setter(TextBlock.TextProperty, new Binding($"Props[{columns[i].Id}]")));
            var style = new Style(typeof(TextBlock), cellStyle);
            style.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(shown);
            var cell = new TextBlock { Style = style, TextAlignment = columns[i].AlignRight ? TextAlignment.Right : TextAlignment.Left };
            Grid.SetColumn(cell, FirstExtraColumn + i);
            RowGrid.Children.Add(cell);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointer.Leave(e); // left the row with the button held: that is a drag too
    }

    // The press is not marked handled here (only the gesture's is): see ItemPointerTracker.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pointer.LeftDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer.Move(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _pointer.LeftUp(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _pointer.RightUp(e);
    }

    // ---- the name cell: where a folder's list hangs -------------------------------------------

    /// <summary>
    /// What a folder's list hangs beside: the name cell (icon + name + 📌), or the whole name column
    /// while 名前列に対してカスケードを表示する is on (<see cref="ICardHost.CascadeOnNameColumn"/>). Both raise
    /// the hover events; only the anchor's count.
    /// </summary>
    public FrameworkElement CascadeAnchor => CardView.Host?.CascadeOnNameColumn == true ? NameColumn : NameCell;

    private void OnNameEnter(object sender, MouseEventArgs e)
    {
        if (_vm is { } vm && ReferenceEquals(sender, CascadeAnchor))
        {
            CardView.Host?.RowMouseEnter(CascadeAnchor, vm);
        }
    }

    private void OnNameLeave(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(sender, CascadeAnchor))
        {
            return;
        }

        _noRearm = false;
        CardView.Host?.RowMouseLeave(CascadeAnchor);
    }

    /// <summary>
    /// A hover starts when the pointer enters the name cell. After the list was closed under a resting
    /// pointer (the window lost activation, the rows changed under it…) a real move asks again.
    /// </summary>
    private void OnNameMove(object sender, MouseEventArgs e)
    {
        if (!_noRearm
            && ReferenceEquals(sender, CascadeAnchor)
            && e.LeftButton == MouseButtonState.Released
            && e.RightButton == MouseButtonState.Released
            && _vm is { } vm)
        {
            CardView.Host?.RowMouseMove(CascadeAnchor, vm);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is { } vm)
        {
            CardView.Host?.ItemRemove(vm);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is { } vm)
        {
            CardView.Host?.ItemUnpin(vm);
        }
    }

    /// <summary>
    /// The container was given another item (rows are recycled while scrolling) or none. The name
    /// column is the same element as before, so a list that was opened (or is about to open) for the
    /// old item must let go of it: nothing else tells the host that the element changed its item.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is not null)
        {
            CardView.Host?.RowMouseLeave(NameCell);
            CardView.Host?.RowMouseLeave(NameColumn);
        }

        (e.OldValue as CardViewModel)?.CancelPropertyReads(); // a recycled row: the old item's reads are not wanted

        _pointer.ItemChanged();
        _vm = e.NewValue as CardViewModel;
    }
}
