using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FolderDeck.Core.Input;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The pointer state of one item element (a card, a row of the details list): which press is a click, when a held press turns
/// into a drag, and the "right button held + left click" gesture. The element forwards the mouse
/// events it sees and the tracker tells the <see cref="ICardHost"/> what they amount to, so every
/// view of an item reads the mouse the same way.
/// <para>
/// What it marks handled is part of the contract: the presses and releases of the gesture, the
/// release that is a click, and every right button release. <b>An ordinary left press is left
/// unhandled</b> and bubbles on, so an ancestor that reacts to left presses must look at
/// <see cref="RoutedEventArgs.OriginalSource"/> to tell a press on an item from one beside it.
/// </para>
/// <para>
/// It works without mouse capture (see <see cref="RockerGesture{T}"/>): the pointer leaving the
/// element ends the press, and counts as a drag when the button is still down.
/// </para>
/// </summary>
public sealed class ItemPointerTracker
{
    private readonly FrameworkElement _element;
    private readonly Func<CardViewModel?> _item;
    private readonly Func<DependencyObject?, bool>? _ownsPress;
    private readonly RockerGesture<CardViewModel> _rocker = new();
    private bool _pressed;
    private bool _dragCandidate;
    private Point _pressedAt;

    /// <summary>The press is the second one of a double click on the item that got the first click.</summary>
    private bool _doubleClick;

    /// <summary>
    /// The item the last plain click on this element went to, and when that click was pressed
    /// (<see cref="InputEventArgs.Timestamp"/>). WPF counts a double click by time and distance only,
    /// whatever was pressed, so the second press must find the same item (a reused element shows
    /// another one) and a first press recent enough to be the one before it (not an old click with
    /// the first press of this double click on something else: a row of the card's overlay).
    /// </summary>
    private CardViewModel? _lastClicked;

    private int _lastClickPressedAt;

    /// <summary>The current press: its time, and whether Ctrl / Shift made it a selection click.</summary>
    private int _pressTime;

    private bool _pressSelecting;

    /// <summary>The double-click time of Windows, in milliseconds (read once).</summary>
    private static readonly int DoubleClickTime = (int)Windows.Win32.PInvoke.GetDoubleClickTime();

    /// <param name="element">The element whose mouse events are forwarded; menus are placed on it.</param>
    /// <param name="item">The item the element shows now (it changes when a container is reused).</param>
    /// <param name="ownsPress">
    /// True for a press on a part of the element that deals with its own presses (the rows of a
    /// card's overlay): it never starts a drag of the item. Buttons are always left out.
    /// </param>
    public ItemPointerTracker(FrameworkElement element, Func<CardViewModel?> item, Func<DependencyObject?, bool>? ownsPress = null)
    {
        _element = element;
        _item = item;
        _ownsPress = ownsPress;
    }

    /// <summary>
    /// The right button went down anywhere on the element (to be called from a tunnelling handler
    /// that also sees handled events): a new press never continues an earlier gesture.
    /// </summary>
    public void RightDown() => _rocker.RightDown();

    public void LeftDown(MouseButtonEventArgs e)
    {
        // Right button held + left click: the item is shown in Explorer once the right button is
        // released. This press is neither a click nor the start of a drag. (A press that a part of
        // the element handled, or one on a button, arms nothing.)
        var source = e.OriginalSource as DependencyObject;
        bool rightHeld = e.RightButton == MouseButtonState.Pressed;
        bool onButton = CardView.FindAncestor<ButtonBase>(source) is not null;
        _doubleClick = false;
        if (_rocker.LeftDown(rightHeld, e.Handled || onButton ? null : _item()))
        {
            _pressed = false;
            _dragCandidate = false;
            e.Handled = true;
            return;
        }

        if (rightHeld)
        {
            _pressed = false; // with the right button down a press is never an ordinary click
            _dragCandidate = false;
            return;
        }

        // A double click must not run twice (§6.8): only the first press counts. A Ctrl / Shift
        // press only changes the selection, so a quick second one must count (to toggle back).
        // When items open on a double click, the first click selects and the second press counts
        // too: on the item of the first click it opens it, on another one it is a first click.
        bool selecting = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        bool byDoubleClick = e.ClickCount == 2 && !selecting && CardView.Host?.OpensOnDoubleClick == true;
        _doubleClick = byDoubleClick
            && _lastClicked is not null
            && ReferenceEquals(_lastClicked, _item())
            && unchecked(e.Timestamp - _lastClickPressedAt) is >= 0 and var elapsed
            && elapsed <= DoubleClickTime;
        _pressTime = e.Timestamp;
        _pressSelecting = selecting;
        _pressed = e.ClickCount == 1 || selecting || byDoubleClick;
        _pressedAt = e.GetPosition(_element);
        _dragCandidate = _pressed && !e.Handled && !onButton && _ownsPress?.Invoke(source) != true;
    }

    public void Move(MouseEventArgs e) => TryStartDrag(e);

    /// <summary>The click is decided here, on the release: a press that became a drag or left the element is none.</summary>
    public void LeftUp(MouseButtonEventArgs e)
    {
        _dragCandidate = false;
        if (_rocker.LeftUp())
        {
            _pressed = false; // the left click of the gesture: waits for the right button
            e.Handled = true;
            return;
        }

        if (_pressed && _element.IsMouseOver)
        {
            _pressed = false;
            e.Handled = true;
            if (_item() is { } item)
            {
                bool doubleClick = _doubleClick;
                _doubleClick = false;

                // Only a plain click can be the first of a double click; after a double click the
                // next quick press starts over (no triple click).
                _lastClicked = doubleClick || _pressSelecting ? null : item;
                _lastClickPressedAt = _pressTime;
                CardView.Host?.ItemClick(item, _element, Keyboard.Modifiers, doubleClick);
            }
        }
    }

    public void RightUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_rocker.RightUp() is { } armed)
        {
            if (ReferenceEquals(armed, _item()))
            {
                CardView.Host?.ItemShowInExplorer(armed);
            }

            return;
        }

        if (_item() is { } item)
        {
            CardView.Host?.ItemContextMenu(item, _element);
        }
    }

    /// <summary>The pointer left the element: with the button held that is a drag too; the press and the gesture end.</summary>
    public void Leave(MouseEventArgs e)
    {
        TryStartDrag(e);
        _pressed = false;
        _dragCandidate = false;
        _rocker.Cancel();
    }

    /// <summary>The element shows another item (or none): a gesture armed on the old one is forgotten.</summary>
    public void ItemChanged()
    {
        _rocker.Cancel();
        _lastClicked = null;
        _doubleClick = false;
    }

    /// <summary>
    /// Starts a drag once the pointer moved past the system drag distance with the button held.
    /// An item the host does not let be dragged keeps its press, which stays a click.
    /// </summary>
    private void TryStartDrag(MouseEventArgs e)
    {
        if (!_dragCandidate || !_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(_element) - _pressedAt;
        if (!FileDrag.IsDragGesture(moved.X, moved.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _dragCandidate = false;
        if (CardView.Host is not { } host || _item() is not { } item || !host.CanDragItem(item))
        {
            return;
        }

        _pressed = false; // a drag never opens the item on mouse up
        host.ItemDrag(item);
    }
}
