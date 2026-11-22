using System.Windows;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// What the main window needs from the view that shows a tab's items, whichever view that is (the
/// card grid or the details list): the scroll position it saves and restores per location, and the
/// reorder drag of the top level. Everything else (items, sizes) is bound in XAML.
/// </summary>
public interface IItemsView
{
    /// <summary>The vertical scroll position, in the view's own unit (not comparable between views).</summary>
    double VerticalOffset { get; }

    /// <summary>Scrolls to <paramref name="offset"/> (a value of <see cref="VerticalOffset"/>), also before the items are laid out.</summary>
    void ScrollTo(double offset);

    /// <summary>Raised when the view scrolled: whatever hangs on one of its items no longer sits beside it.</summary>
    event EventHandler? Scrolled;

    /// <summary>
    /// Drags <paramref name="card"/> to a new position (blocks until the drag ends) and returns the
    /// insertion index among the items (0…Count, counted with the item still in place), or null when
    /// the drag was cancelled or released outside the view. <paramref name="doDragDrop"/> runs the
    /// drag with the source the view gives it.
    /// </summary>
    int? RunReorderDrag(CardViewModel card, Func<DependencyObject, DragDropEffects> doDragDrop);
}
