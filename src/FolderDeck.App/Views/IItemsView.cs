using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>
    /// The realized item view (<see cref="CardView"/> / <see cref="DetailsRowView"/>) under the
    /// pointer, found by position alone: it does not depend on IsHitTestVisible (the views stop
    /// hit testing while a tab loads). Null outside the scroll area.
    /// </summary>
    FrameworkElement? RealizedItemAt(MouseEventArgs e);
}

/// <summary>The geometric search behind <see cref="IItemsView.RealizedItemAt"/>.</summary>
internal static class ItemsViewHitTest
{
    public static FrameworkElement? Find(ScrollViewer? scroll, Visual root, MouseEventArgs e)
    {
        if (scroll is not { IsVisible: true })
        {
            return null;
        }

        // Only the viewport counts: not the details header above it, nor items the virtualizing
        // panel keeps realized just outside it.
        var inScroll = e.GetPosition(scroll);
        if (inScroll.X < 0 || inScroll.Y < 0 || inScroll.X >= scroll.ActualWidth || inScroll.Y >= scroll.ActualHeight)
        {
            return null;
        }

        return Search(root, e);

        static FrameworkElement? Search(DependencyObject node, MouseEventArgs e)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is CardView or DetailsRowView)
                {
                    var view = (FrameworkElement)child;
                    if (view.IsVisible)
                    {
                        var p = e.GetPosition(view);
                        if (p.X >= 0 && p.Y >= 0 && p.X < view.ActualWidth && p.Y < view.ActualHeight)
                        {
                            return view;
                        }
                    }

                    continue; // an item view holds no other item views
                }

                if (Search(child, e) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
