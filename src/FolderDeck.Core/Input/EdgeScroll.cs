namespace FolderDeck.Core.Input;

/// <summary>
/// Auto-scroll of a marquee drag: the list scrolls while the pointer (held by mouse capture) is
/// past the top or bottom edge of the viewport, faster the further out it is.
/// </summary>
public static class EdgeScroll
{
    /// <summary>
    /// The scroll step for a pointer at <paramref name="y"/> in a viewport of <paramref name="height"/>:
    /// 0 while inset ≤ y ≤ height − inset; past that band ±maxStep × clamp(distance / ramp, 0.15, 1)
    /// (negative: up). 0 for a viewport without height.
    /// </summary>
    public static double OutsideStep(double y, double height, double inset, double ramp, double maxStep)
    {
        if (!(height > 0) || !double.IsFinite(y) || !(ramp > 0))
        {
            return 0;
        }

        double distance;
        double sign;
        if (y < inset)
        {
            distance = inset - y;
            sign = -1;
        }
        else if (y > height - inset)
        {
            distance = y - (height - inset);
            sign = 1;
        }
        else
        {
            return 0;
        }

        return sign * maxStep * Math.Clamp(distance / ramp, 0.15, 1);
    }
}
