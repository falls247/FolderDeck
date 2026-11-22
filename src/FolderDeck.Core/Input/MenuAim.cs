namespace FolderDeck.Core.Input;

/// <summary>
/// Menu aim (the "safe triangle"): tells whether the pointer, crossing the rows of a list, is on
/// its way to what is open beside the list (a child list, a preview). The rows it crosses on that
/// way must neither open their own child nor close the open one; once the pointer rests or turns
/// away, the usual hover rules apply again.
/// <para>
/// The pointer heads for the target when its latest position lies inside the triangle between an
/// earlier position and the two ends of the target's edge that faces the list. The view feeds the
/// pointer moves it sees (screen pixels, from any of its windows) and asks only when a hover
/// decision is due; nothing polls.
/// </para>
/// </summary>
public sealed class MenuAim
{
    /// <summary>Moves kept. The earlier position is the oldest of them: a few moves back evens out single-pixel steps.</summary>
    private const int Kept = 4;

    private readonly Sample[] _samples = new Sample[Kept];
    private int _count;
    private int _next;

    /// <summary>
    /// The pointer is at (<paramref name="x"/>, <paramref name="y"/>) at <paramref name="time"/>
    /// (milliseconds, e.g. <see cref="Environment.TickCount64"/>). The same position as the last one
    /// is not a move (a UI raises mouse moves when the element under a resting pointer changes) and
    /// is ignored, so a resting pointer keeps the time of its last real move.
    /// </summary>
    public void Record(double x, double y, long time)
    {
        if (_count > 0 && Latest.X == x && Latest.Y == y)
        {
            return;
        }

        _samples[_next] = new Sample(x, y, time);
        _next = (_next + 1) % Kept;
        _count = Math.Min(_count + 1, Kept);
    }

    /// <summary>Forgets every move.</summary>
    public void Clear()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>
    /// True when the pointer moved within the last <paramref name="movingWithin"/> ms before
    /// <paramref name="now"/> and is heading for the target rectangle: its latest position lies in
    /// the triangle between an earlier one and the target's edge that faces it (the left edge of a
    /// target on the right, the right edge when <paramref name="targetOnLeft"/>), the edge lengthened
    /// by <paramref name="slack"/> at both ends. A pointer that rests, moves away, moves along the
    /// list (straight up or down) or has passed the edge is not heading for it.
    /// </summary>
    public bool IsHeadingFor(double left, double top, double right, double bottom, bool targetOnLeft, long now, long movingWithin, double slack)
    {
        if (_count < 2 || now - Latest.Time > movingWithin)
        {
            return false; // no move yet, or resting: the decision is due
        }

        var earlier = _samples[(_next + Kept - _count) % Kept];
        var latest = Latest;
        if (latest.X == earlier.X && latest.Y == earlier.Y)
        {
            return false; // back where it was (jitter): not a heading
        }

        double edge = targetOnLeft ? right : left;
        if (targetOnLeft ? earlier.X <= edge : earlier.X >= edge)
        {
            return false; // started on the target's side of the edge: not approaching that edge
        }

        return InTriangle(latest.X, latest.Y, earlier.X, earlier.Y, edge, top - slack, edge, bottom + slack);
    }

    private Sample Latest => _samples[(_next + Kept - 1) % Kept];

    /// <summary>(x, y) lies in the triangle a-b-c, its sides included.</summary>
    private static bool InTriangle(double x, double y, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = Side(ax, ay, bx, by, x, y);
        double d2 = Side(bx, by, cx, cy, x, y);
        double d3 = Side(cx, cy, ax, ay, x, y);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>Which side of the line p→q the point (x, y) is on (the sign of the cross product).</summary>
    private static double Side(double px, double py, double qx, double qy, double x, double y) =>
        ((qx - px) * (y - py)) - ((qy - py) * (x - px));

    private readonly record struct Sample(double X, double Y, long Time);
}
