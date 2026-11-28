namespace FolderDeck.Core.Layout;

/// <summary>How a split lays out its two parts.</summary>
public enum SplitAxis
{
    /// <summary>Side by side: <see cref="PaneSplit{T}.First"/> on the left, <see cref="PaneSplit{T}.Second"/> on the right.</summary>
    Columns,

    /// <summary>Stacked: <see cref="PaneSplit{T}.First"/> above, <see cref="PaneSplit{T}.Second"/> below.</summary>
    Rows,
}

/// <summary>A node of a <see cref="PaneLayout{T}"/>: one pane (<see cref="PaneLeaf{T}"/>) or a split of two nodes (<see cref="PaneSplit{T}"/>).</summary>
public abstract class PaneNode<T>
    where T : class
{
    private protected PaneNode()
    {
    }
}

/// <summary>One pane of a tab.</summary>
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
/// How a tab arranges its panes (as Windows Terminal or tmux): a binary tree whose leaves are the
/// panes, each pane once. Immutable: a change makes a new layout. A tab that is not split is one
/// leaf. <typeparamref name="T"/> is the pane (the app's <c>PaneViewModel</c>); the layout compares
/// panes by reference.
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

    /// <summary>A layout of the one pane <paramref name="pane"/> (a tab that is not split).</summary>
    public static PaneLayout<T> Single(T pane) => new(new PaneLeaf<T>(pane));

    public PaneNode<T> Root { get; }

    /// <summary>
    /// Every pane in reading order: of a split, the first part's panes before the second's (left
    /// before right, top before bottom).
    /// </summary>
    public IReadOnlyList<T> Panes { get; }

    public bool Contains(T pane) => Panes.Any(p => ReferenceEquals(p, pane));

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
