namespace FolderDeck.Core.Layout;

/// <summary>How a split lays out its two parts.</summary>
public enum SplitAxis
{
    /// <summary>Side by side: <see cref="PaneSplit{T}.First"/> on the left, <see cref="PaneSplit{T}.Second"/> on the right.</summary>
    Columns,

    /// <summary>Stacked: <see cref="PaneSplit{T}.First"/> above, <see cref="PaneSplit{T}.Second"/> below.</summary>
    Rows,
}

/// <summary>A way to move from a pane to the one next to it (<see cref="PaneLayout{T}.Neighbor"/>).</summary>
public enum PaneDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>A node of a <see cref="PaneLayout{T}"/>: one pane (<see cref="PaneLeaf{T}"/>) or a split of two nodes (<see cref="PaneSplit{T}"/>).</summary>
public abstract class PaneNode<T>
    where T : class
{
    private protected PaneNode()
    {
    }
}

/// <summary>One pane of the split.</summary>
public sealed class PaneLeaf<T>(T pane) : PaneNode<T>
    where T : class
{
    public T Pane { get; } = pane ?? throw new ArgumentNullException(nameof(pane));
}

/// <summary>
/// Two nodes sharing the space of this one along <see cref="Axis"/>: <see cref="First"/> takes
/// <see cref="Ratio"/> of it, <see cref="Second"/> the rest.
/// </summary>
public sealed class PaneSplit<T>(SplitAxis axis, double ratio, PaneNode<T> first, PaneNode<T> second) : PaneNode<T>
    where T : class
{
    public SplitAxis Axis { get; } = axis;

    public double Ratio { get; } = ratio;

    public PaneNode<T> First { get; } = first ?? throw new ArgumentNullException(nameof(first));

    public PaneNode<T> Second { get; } = second ?? throw new ArgumentNullException(nameof(second));
}

/// <summary>
/// How the window splits its space into panes (as Windows Terminal or tmux): a binary tree whose
/// leaves are the panes, each pane once. Immutable: a change makes a new layout (the nodes of the parts
/// it does not touch are kept). A window that is not split is one leaf. <typeparamref name="T"/> is
/// the pane (in the app, the tab the pane shows); the layout compares panes by reference.
/// </summary>
public sealed class PaneLayout<T>
    where T : class
{
    public PaneLayout(PaneNode<T> root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        var panes = new List<T>();
        Collect(root, panes);
        if (panes.Distinct(ReferenceEqualityComparer.Instance).Count() != panes.Count)
        {
            throw new ArgumentException("A pane is in the layout more than once.", nameof(root));
        }

        Panes = panes;
    }

    /// <summary>A layout of the one pane <paramref name="pane"/> (a window that is not split).</summary>
    public static PaneLayout<T> Single(T pane) => new(new PaneLeaf<T>(pane));

    public PaneNode<T> Root { get; }

    /// <summary>
    /// Every pane in reading order: of a split, the first part's panes before the second's (left
    /// before right, top before bottom).
    /// </summary>
    public IReadOnlyList<T> Panes { get; }

    public bool Contains(T pane) => Panes.Any(p => ReferenceEquals(p, pane));

    /// <summary>
    /// <paramref name="target"/> shares its space with the new pane <paramref name="added"/>, half each:
    /// <paramref name="added"/> goes right of (<see cref="SplitAxis.Columns"/>) or below
    /// (<see cref="SplitAxis.Rows"/>) the target. The size rules are <see cref="PaneRules.CanSplit"/>'s.
    /// </summary>
    public PaneLayout<T> Split(T target, SplitAxis axis, T added)
    {
        ArgumentNullException.ThrowIfNull(added);
        RequireContains(target, nameof(target));
        if (Contains(added))
        {
            throw new ArgumentException("The pane is already in the layout.", nameof(added));
        }

        return new(Rebuild(Root, node => node is PaneLeaf<T> leaf && ReferenceEquals(leaf.Pane, target)
            ? new PaneSplit<T>(axis, 0.5, leaf, new PaneLeaf<T>(added))
            : null));
    }

    /// <summary>
    /// The same layout with <paramref name="replacement"/> where <paramref name="old"/> was (same place,
    /// same ratios; the nodes of the other parts are kept). <paramref name="replacement"/> must not be in
    /// the layout already, unless it is <paramref name="old"/> itself (then this layout).
    /// </summary>
    public PaneLayout<T> Replace(T old, T replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        RequireContains(old, nameof(old));
        if (ReferenceEquals(old, replacement))
        {
            return this;
        }

        if (Contains(replacement))
        {
            throw new ArgumentException("The pane is already in the layout.", nameof(replacement));
        }

        return new(Rebuild(Root, node => node is PaneLeaf<T> leaf && ReferenceEquals(leaf.Pane, old)
            ? new PaneLeaf<T>(replacement)
            : null));
    }

    /// <summary>
    /// Where each pane is when the layout fills the unit square (0–1 both ways) by its ratios, the
    /// splitters ignored; in reading order (<see cref="Panes"/>).
    /// </summary>
    public IReadOnlyList<(T Pane, double X, double Y, double Width, double Height)> Arrange()
    {
        var into = new List<(T Pane, double X, double Y, double Width, double Height)>(Panes.Count);
        void Place(PaneNode<T> node, double x, double y, double width, double height)
        {
            switch (node)
            {
                case PaneLeaf<T> leaf:
                    into.Add((leaf.Pane, x, y, width, height));
                    break;
                case PaneSplit<T> split when split.Axis == SplitAxis.Columns:
                    double w = width * split.Ratio;
                    Place(split.First, x, y, w, height);
                    Place(split.Second, x + w, y, width - w, height);
                    break;
                case PaneSplit<T> split:
                    double h = height * split.Ratio;
                    Place(split.First, x, y, width, h);
                    Place(split.Second, x, y + h, width, height - h);
                    break;
            }
        }

        Place(Root, 0, 0, 1, 1);
        return into;
    }

    /// <summary>
    /// The pane next to <paramref name="pane"/> in <paramref name="direction"/> (moving the focus as in
    /// Windows Terminal): of the panes whose rectangle (<see cref="Arrange"/>) shares that edge of
    /// <paramref name="pane"/>'s, the one sharing the longest part of it; of equal ones, the one whose
    /// shared part starts first (topmost for left / right, leftmost for up / down). Null at the edge of
    /// the layout (no wrapping).
    /// </summary>
    public T? Neighbor(T pane, PaneDirection direction)
    {
        const double Epsilon = 1e-9;
        RequireContains(pane, nameof(pane));
        var rects = Arrange();
        var from = rects.First(r => ReferenceEquals(r.Pane, pane));
        bool horizontal = direction is PaneDirection.Left or PaneDirection.Right;
        T? best = default;
        double bestOverlap = 0;
        double bestStart = 0;
        foreach (var r in rects)
        {
            if (ReferenceEquals(r.Pane, pane))
            {
                continue;
            }

            bool touches = direction switch
            {
                PaneDirection.Left => Math.Abs((r.X + r.Width) - from.X) < Epsilon,
                PaneDirection.Right => Math.Abs(r.X - (from.X + from.Width)) < Epsilon,
                PaneDirection.Up => Math.Abs((r.Y + r.Height) - from.Y) < Epsilon,
                _ => Math.Abs(r.Y - (from.Y + from.Height)) < Epsilon,
            };
            if (!touches)
            {
                continue;
            }

            double start = horizontal ? Math.Max(r.Y, from.Y) : Math.Max(r.X, from.X);
            double end = horizontal ? Math.Min(r.Y + r.Height, from.Y + from.Height) : Math.Min(r.X + r.Width, from.X + from.Width);
            double overlap = end - start;
            if (overlap <= Epsilon)
            {
                continue; // only a corner in common
            }

            if (best is null || overlap > bestOverlap + Epsilon || (Math.Abs(overlap - bestOverlap) <= Epsilon && start < bestStart))
            {
                best = r.Pane;
                bestOverlap = overlap;
                bestStart = start;
            }
        }

        return best;
    }

    /// <summary>
    /// The layout without <paramref name="pane"/>: the other part of its split takes its space. Null
    /// when <paramref name="pane"/> is the only pane. <paramref name="next"/> is the pane
    /// to work in now: the one of the other part nearest to where the closed pane was (its first pane
    /// when the closed one came first, its last pane when it came second).
    /// </summary>
    public PaneLayout<T>? Close(T pane, out T? next)
    {
        RequireContains(pane, nameof(pane));
        next = default;
        if (Root is PaneLeaf<T>)
        {
            return null;
        }

        T? nearest = default;
        var root = Rebuild(Root, node =>
        {
            if (node is not PaneSplit<T> split)
            {
                return null;
            }

            if (split.First is PaneLeaf<T> first && ReferenceEquals(first.Pane, pane))
            {
                nearest = Edge(split.Second, last: false);
                return split.Second;
            }

            if (split.Second is PaneLeaf<T> second && ReferenceEquals(second.Pane, pane))
            {
                nearest = Edge(split.First, last: true);
                return split.First;
            }

            return null;
        });
        next = nearest;
        return new(root);
    }

    /// <summary>
    /// The same layout with <paramref name="split"/> (a node of this layout) at <paramref name="ratio"/>,
    /// pulled into <see cref="PaneRules.MinRatio"/>–<see cref="PaneRules.MaxRatio"/>. The minimum pane
    /// sizes are <see cref="PaneRules.ClampRatio"/>'s, which the caller applies first.
    /// </summary>
    public PaneLayout<T> WithRatio(PaneSplit<T> split, double ratio)
    {
        ArgumentNullException.ThrowIfNull(split);
        bool found = false;
        var root = Rebuild(Root, node =>
        {
            if (!ReferenceEquals(node, split))
            {
                return null;
            }

            found = true;
            return new PaneSplit<T>(split.Axis, PaneRules.NormalizeRatio(ratio), split.First, split.Second);
        });
        return found ? new(root) : throw new ArgumentException("The split is not in the layout.", nameof(split));
    }

    /// <summary>The same shape and ratios with each pane replaced by <paramref name="map"/>'s result.</summary>
    public PaneLayout<TOut> Map<TOut>(Func<T, TOut> map)
        where TOut : class
    {
        ArgumentNullException.ThrowIfNull(map);
        PaneNode<TOut> Of(PaneNode<T> node) => node switch
        {
            PaneLeaf<T> leaf => new PaneLeaf<TOut>(map(leaf.Pane)),
            PaneSplit<T> split => new PaneSplit<TOut>(split.Axis, split.Ratio, Of(split.First), Of(split.Second)),
            _ => throw new InvalidOperationException(),
        };
        return new(Of(Root));
    }

    /// <summary>The pane after (<paramref name="back"/>: before) <paramref name="pane"/> in reading order, wrapping around (F6 / Shift+F6).</summary>
    public T Next(T pane, bool back)
    {
        int index = IndexOf(pane);
        if (index < 0)
        {
            throw new ArgumentException("The pane is not in the layout.", nameof(pane));
        }

        return Panes[(index + (back ? Panes.Count - 1 : 1)) % Panes.Count];
    }

    private int IndexOf(T pane)
    {
        for (int i = 0; i < Panes.Count; i++)
        {
            if (ReferenceEquals(Panes[i], pane))
            {
                return i;
            }
        }

        return -1;
    }

    private void RequireContains(T pane, string name)
    {
        if (!Contains(pane))
        {
            throw new ArgumentException("The pane is not in the layout.", name);
        }
    }

    /// <summary>
    /// The tree with the first node (top down, in reading order) that <paramref name="replace"/> gives a
    /// node for replaced by it; a split none of whose parts changed is kept as it is.
    /// </summary>
    private static PaneNode<T> Rebuild(PaneNode<T> node, Func<PaneNode<T>, PaneNode<T>?> replace)
    {
        if (replace(node) is { } replaced)
        {
            return replaced;
        }

        if (node is not PaneSplit<T> split)
        {
            return node;
        }

        var first = Rebuild(split.First, replace);
        var second = ReferenceEquals(first, split.First) ? Rebuild(split.Second, replace) : split.Second;
        return ReferenceEquals(first, split.First) && ReferenceEquals(second, split.Second)
            ? split
            : new PaneSplit<T>(split.Axis, split.Ratio, first, second);
    }

    /// <summary>The first (or <paramref name="last"/>) pane of <paramref name="node"/> in reading order.</summary>
    private static T Edge(PaneNode<T> node, bool last)
    {
        while (node is PaneSplit<T> split)
        {
            node = last ? split.Second : split.First;
        }

        return ((PaneLeaf<T>)node).Pane;
    }

    private static void Collect(PaneNode<T> node, List<T> into)
    {
        switch (node)
        {
            case PaneLeaf<T> leaf:
                into.Add(leaf.Pane);
                break;
            case PaneSplit<T> split:
                Collect(split.First, into);
                Collect(split.Second, into);
                break;
        }
    }
}

/// <summary>Why a pane cannot be split (<see cref="PaneRules.CanSplit"/>).</summary>
public enum SplitRefusal
{
    None,

    /// <summary>The window already has <see cref="PaneRules.MaxPanes"/> panes.</summary>
    TooManyPanes,

    /// <summary>Side by side, the two halves would be narrower than <see cref="PaneRules.MinWidth"/>.</summary>
    TooNarrow,

    /// <summary>Stacked, the two halves would be lower than <see cref="PaneRules.MinHeight"/>.</summary>
    TooLow,
}

/// <summary>
/// Size rules of the panes of the split (DIP). A pane is never hidden or closed because the window got
/// small: below the minimum sizes the panes shrink together.
/// </summary>
public static class PaneRules
{
    public const int MaxPanes = 4;

    public const double MinWidth = 280;

    public const double MinHeight = 160;

    /// <summary>The bar between the two parts of a split (dragged to resize them).</summary>
    public const double SplitterThickness = 5;

    /// <summary>Range of <see cref="PaneSplit{T}.Ratio"/> whatever the size of the window.</summary>
    public const double MinRatio = 0.05;

    public const double MaxRatio = 0.95;

    public const double DefaultRatio = 0.5;

    /// <summary>Whether a pane of <paramref name="paneWidth"/> × <paramref name="paneHeight"/> in a split of <paramref name="paneCount"/> panes can be split along <paramref name="axis"/>.</summary>
    public static SplitRefusal CanSplit(int paneCount, double paneWidth, double paneHeight, SplitAxis axis)
    {
        if (paneCount >= MaxPanes)
        {
            return SplitRefusal.TooManyPanes;
        }

        return axis == SplitAxis.Columns
            ? paneWidth >= (2 * MinWidth) + SplitterThickness ? SplitRefusal.None : SplitRefusal.TooNarrow
            : paneHeight >= (2 * MinHeight) + SplitterThickness ? SplitRefusal.None : SplitRefusal.TooLow;
    }

    /// <summary>The least extent along <paramref name="axis"/> that <paramref name="node"/> needs for every pane in it to keep its minimum size.</summary>
    public static double MinExtent<T>(PaneNode<T> node, SplitAxis axis)
        where T : class => node switch
        {
            PaneSplit<T> split when split.Axis == axis => MinExtent(split.First, axis) + SplitterThickness + MinExtent(split.Second, axis),
            PaneSplit<T> split => Math.Max(MinExtent(split.First, axis), MinExtent(split.Second, axis)),
            _ => axis == SplitAxis.Columns ? MinWidth : MinHeight,
        };

    /// <summary>
    /// <paramref name="ratio"/> for <paramref name="split"/> whose whole extent along its axis (the
    /// splitter included) is <paramref name="extent"/>, pulled in so that both parts keep their
    /// <see cref="MinExtent"/>. When the extent is too small for both, only the range of
    /// <see cref="NormalizeRatio"/> applies (the parts shrink below their minimum together).
    /// </summary>
    public static double ClampRatio<T>(PaneSplit<T> split, double extent, double ratio)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(split);
        double value = NormalizeRatio(ratio);
        double available = extent - SplitterThickness;
        double minFirst = MinExtent(split.First, split.Axis);
        double minSecond = MinExtent(split.Second, split.Axis);
        if (!double.IsFinite(available) || available <= 0 || minFirst + minSecond > available)
        {
            return value;
        }

        return NormalizeRatio(Math.Clamp(value, minFirst / available, 1 - (minSecond / available)));
    }

    /// <summary><paramref name="ratio"/> pulled into <see cref="MinRatio"/>–<see cref="MaxRatio"/> (not a number: <see cref="DefaultRatio"/>).</summary>
    public static double NormalizeRatio(double ratio) => double.IsNaN(ratio) ? DefaultRatio : Math.Clamp(ratio, MinRatio, MaxRatio);
}
