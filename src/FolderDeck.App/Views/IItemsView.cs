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

    /// <summary>
    /// Scrolls <paramref name="item"/> into view and returns its realized item view
    /// (<see cref="CardView"/> / <see cref="DetailsRowView"/>), or null (not one of the view's items,
    /// the view is hidden or not laid out yet). The scroll happens inside this call, so
    /// <see cref="Scrolled"/> is raised before it returns.
    /// </summary>
    FrameworkElement? BringItemIntoView(CardViewModel item);
}

/// <summary>The common part of <see cref="IItemsView.BringItemIntoView"/>.</summary>
internal static class ItemsViewReveal
{
    /// <summary>
    /// Brings the container at <paramref name="index"/> of <paramref name="rowsHost"/> (a row of
    /// cards, a row of the details list) into view through its virtualizing panel, lays it out and
    /// returns the item view of <paramref name="item"/> inside it.
    /// </summary>
    public static FrameworkElement? Reveal(ItemsControl rowsHost, VirtualizingStackPanel? panel, int index, CardViewModel item)
    {
        // BringIndexIntoViewPublic throws outside 0…Count-1.
        if (panel is null || index < 0 || index >= rowsHost.Items.Count)
        {
            return null;
        }

        panel.BringIndexIntoViewPublic(index);
        rowsHost.UpdateLayout(); // runs the scroll viewer's queued MakeVisible and realizes the container
        return rowsHost.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container
            ? FindView(container, item)
            : null;
    }

    private static FrameworkElement? FindView(DependencyObject node, CardViewModel item)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            switch (child)
            {
                case CardView card:
                    if (ReferenceEquals(card.ViewModel, item))
                    {
                        return card;
                    }

                    continue; // an item view holds no other item views
                case DetailsRowView row:
                    if (ReferenceEquals(row.ViewModel, item))
                    {
                        return row;
                    }

                    continue;
            }

            if (FindView(child, item) is { } found)
            {
                return found;
            }
        }

        return null;
    }
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
