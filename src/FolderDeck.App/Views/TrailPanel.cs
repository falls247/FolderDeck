using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FolderDeck.Views;

/// <summary>
/// The address bar's crumbs in a row. When they do not fit, the first ones are left out so the end of
/// the path stays in view (the last crumb alone trims its text), the row starts after
/// <see cref="LeadWidth"/> (room for the "…" that lies over it), and <see cref="IsTrimmedProperty"/> is
/// set on the ItemsControl that owns the panel (it shows the "…"). Whether everything fits is always
/// judged on the whole width, so the "…" goes away again as soon as the whole path fits.
/// </summary>
public sealed class TrailPanel : Panel
{
    public static readonly DependencyProperty IsTrimmedProperty = DependencyProperty.RegisterAttached(
        "IsTrimmed", typeof(bool), typeof(TrailPanel), new PropertyMetadata(false));

    public static readonly DependencyProperty LeadWidthProperty = DependencyProperty.Register(
        nameof(LeadWidth), typeof(double), typeof(TrailPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The room kept free at the start of the row while crumbs are left out (for the "…").</summary>
    public double LeadWidth
    {
        get => (double)GetValue(LeadWidthProperty);
        set => SetValue(LeadWidthProperty, value);
    }

    /// <summary>Where the shown crumbs start (0, or <see cref="LeadWidth"/> while trimmed).</summary>
    private double _start;

    /// <summary>The first child that is shown (those before it are left out).</summary>
    private int _first;

    public static bool GetIsTrimmed(DependencyObject element) => (bool)element.GetValue(IsTrimmedProperty);

    public static void SetIsTrimmed(DependencyObject element, bool value) => element.SetValue(IsTrimmedProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        double height = 0;
        double total = 0;
        foreach (UIElement child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            total += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        int first = 0;
        double room = availableSize.Width;
        if (total > room)
        {
            room = Math.Max(0, availableSize.Width - LeadWidth); // trimmed: the "…" takes the start
            while (first < children.Count - 1 && total > room)
            {
                total -= children[first].DesiredSize.Width;
                first++;
            }

            if (children.Count > 0 && total > room)
            {
                var last = children[^1];
                last.Measure(new Size(room, availableSize.Height)); // too wide on its own: its text trims
                total = Math.Min(last.DesiredSize.Width, room);
            }
        }

        _first = first;
        _start = first > 0 ? Math.Min(LeadWidth, availableSize.Width) : 0;
        total += _start;
        if (ItemsControl.GetItemsOwner(this) is { } owner && GetIsTrimmed(owner) != first > 0)
        {
            SetIsTrimmed(owner, first > 0);
        }

        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        double x = _start;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];

            // Left out: far outside the clipped bar, so it is neither drawn nor hit, and Tab skips its button.
            KeyboardNavigation.SetTabNavigation(child, i < _first ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
            if (i < _first)
            {
                child.Arrange(new Rect(-100_000, 0, child.DesiredSize.Width, finalSize.Height));
                continue;
            }

            double width = Math.Max(0, Math.Min(child.DesiredSize.Width, finalSize.Width - x));
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }
}
