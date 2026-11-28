using FolderDeck.Core.Layout;

namespace FolderDeck.Core.Stores;

/// <summary>
/// Converts a <see cref="PaneLayout{T}"/> to and from its settings.json form
/// (<see cref="OpenTabSetting.Layout"/> with <see cref="OpenTabSetting.Panes"/>), and holds the one
/// check of that form that both <see cref="AppSettings.Normalize"/> and <see cref="FromSetting"/> use.
/// </summary>
public static class PaneLayoutSettings
{
    public const string Columns = "columns";
    public const string Rows = "rows";

    public static string ToSetting(SplitAxis axis) => axis == SplitAxis.Rows ? Rows : Columns;

    /// <summary>The axis of a <see cref="PaneNodeSetting.Split"/> value (case and spaces ignored); null for anything else.</summary>
    public static SplitAxis? AxisOf(string? split) => split?.Trim().ToLowerInvariant() switch
    {
        Columns => SplitAxis.Columns,
        Rows => SplitAxis.Rows,
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="node"/> is a layout of <paramref name="paneCount"/> panes: 2 to
    /// <see cref="PaneRules.MaxPanes"/> panes; every leaf has a pane index in range and no children;
    /// every split has a known <see cref="PaneNodeSetting.Split"/>, both children and no pane index;
    /// each index is used exactly once (so every pane is in the layout).
    /// </summary>
    public static bool IsValid(PaneNodeSetting? node, int paneCount)
    {
        if (node is null || paneCount < 2 || paneCount > PaneRules.MaxPanes)
        {
            return false;
        }

        var used = new bool[paneCount];
        int leaves = 0;
        return Check(node, 0) && leaves == paneCount;

        bool Check(PaneNodeSetting n, int depth)
        {
            if (depth >= paneCount)
            {
                return false; // a tree of paneCount leaves is never this deep
            }

            if (n.Split is null)
            {
                if (n.First is not null || n.Second is not null || n.Pane is not int index || index < 0 || index >= paneCount || used[index])
                {
                    return false;
                }

                used[index] = true;
                leaves++;
                return true;
            }

            return AxisOf(n.Split) is not null && n.Pane is null && n.First is not null && n.Second is not null
                && Check(n.First, depth + 1) && Check(n.Second, depth + 1);
        }
    }

    /// <summary>
    /// The form of <paramref name="layout"/> whose leaves are indexes into <paramref name="order"/>
    /// (normally <see cref="PaneLayout{T}.Panes"/>, the order the panes are written in).
    /// </summary>
    public static PaneNodeSetting ToSetting<T>(PaneLayout<T> layout, IReadOnlyList<T> order)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(order);

        PaneNodeSetting Of(PaneNode<T> node)
        {
            switch (node)
            {
                case PaneLeaf<T> leaf:
                    for (int i = 0; i < order.Count; i++)
                    {
                        if (ReferenceEquals(order[i], leaf.Pane))
                        {
                            return new PaneNodeSetting { Pane = i };
                        }
                    }

                    throw new ArgumentException("A pane of the layout is not in the order.", nameof(order));
                case PaneSplit<T> split:
                    return new PaneNodeSetting { Split = ToSetting(split.Axis), Ratio = split.Ratio, First = Of(split.First), Second = Of(split.Second) };
                default:
                    throw new InvalidOperationException();
            }
        }

        return Of(layout.Root);
    }

    /// <summary>
    /// The layout that <paramref name="node"/> describes, with <paramref name="panes"/> as its panes (leaf
    /// <c>n</c> is <c>panes[n]</c>; ratios pulled into range). Null when <paramref name="node"/> is not
    /// <see cref="IsValid"/> for that many panes or a pane is given twice: the tab is then not split.
    /// </summary>
    public static PaneLayout<T>? FromSetting<T>(PaneNodeSetting? node, IReadOnlyList<T> panes)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(panes);
        if (!IsValid(node, panes.Count) || panes.Distinct(ReferenceEqualityComparer.Instance).Count() != panes.Count)
        {
            return null;
        }

        PaneNode<T> Of(PaneNodeSetting n) => n.Split is null
            ? new PaneLeaf<T>(panes[n.Pane!.Value])
            : new PaneSplit<T>(AxisOf(n.Split)!.Value, PaneRules.NormalizeRatio(n.Ratio ?? PaneRules.DefaultRatio), Of(n.First!), Of(n.Second!));
        return new PaneLayout<T>(Of(node!));
    }

    /// <summary>A copy of the valid (<see cref="IsValid"/>) <paramref name="node"/> with the split values in their written form and the ratios in range.</summary>
    internal static PaneNodeSetting Normalize(PaneNodeSetting node) => node.Split is null
        ? new PaneNodeSetting { Pane = node.Pane }
        : new PaneNodeSetting
        {
            Split = ToSetting(AxisOf(node.Split)!.Value),
            Ratio = PaneRules.NormalizeRatio(node.Ratio ?? PaneRules.DefaultRatio),
            First = Normalize(node.First!),
            Second = Normalize(node.Second!),
        };
}
