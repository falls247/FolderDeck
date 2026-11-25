using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// A button went down on the row (a click, a menu, a drag): moving on the name column does not
    /// ask for the folder's list again until the pointer has left the column (as on a sidebar row).
    /// </summary>
    private bool _noRearm;

    /// <summary>One automation element with the item's name, nothing inside (<see cref="ItemViewAutomationPeer"/>).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ItemViewAutomationPeer(this);

    public DetailsRowView()
    {
        InitializeComponent();
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

    // ---- the name column: where a folder's list hangs -----------------------------------------

    private void OnNameEnter(object sender, MouseEventArgs e)
    {
        if (_vm is { } vm)
        {
            CardView.Host?.RowMouseEnter(NameColumn, vm);
        }
    }

    private void OnNameLeave(object sender, MouseEventArgs e)
    {
        _noRearm = false;
        CardView.Host?.RowMouseLeave(NameColumn);
    }

    /// <summary>
    /// A hover starts when the pointer enters the name column. After the list was closed under a resting
    /// pointer (the window lost activation, the rows changed under it…) a real move asks again.
    /// </summary>
    private void OnNameMove(object sender, MouseEventArgs e)
    {
        if (!_noRearm
            && e.LeftButton == MouseButtonState.Released
            && e.RightButton == MouseButtonState.Released
            && _vm is { } vm)
        {
            CardView.Host?.RowMouseMove(NameColumn, vm);
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
            CardView.Host?.RowMouseLeave(NameColumn);
        }

        _pointer.ItemChanged();
        _vm = e.NewValue as CardViewModel;
    }
}
