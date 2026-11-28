using FolderDeck.Core.Layout;

namespace FolderDeck.Core.Stores;

/// <summary>
/// Converts a <see cref="PaneLayout{T}"/> to and from its settings.json form (<see cref="AppSettings.SplitLayout"/>,
/// whose leaves are indexes into <see cref="AppSettings.OpenTabs"/>), and holds the one check of that
/// form that both <see cref="AppSettings.Normalize"/> and <see cref="FromSetting"/> use.
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
    /// Whether <paramref name="node"/> is a split of some of <paramref name="itemCount"/> items (the open
    /// tabs): 2 to <see cref="PaneRules.MaxPanes"/> leaves; every leaf has an index in range and no
    /// children; every split has a known <see cref="PaneNodeSetting.Split"/>, both children and no
    /// index; no index twice. Items without a leaf are fine (tabs that are not shown).
    /// </summary>
    public static bool IsValid(PaneNodeSetting? node, int itemCount)
    {
        if (node is null || itemCount < 2)
        {
            return false;
        }

        var used = new HashSet<int>();
        return Check(node, 0) && used.Count >= 2;

        bool Check(PaneNodeSetting n, int depth)
        {
            if (depth >= PaneRules.MaxPanes)
            {
                return false; // a tree of MaxPanes leaves is never this deep
            }

            if (n.Split is null)
            {
                return n.First is null && n.Second is null
                    && n.Pane is int index && index >= 0 && index < itemCount
                    && used.Count < PaneRules.MaxPanes && used.Add(index);
            }

            return AxisOf(n.Split) is not null && n.Pane is null && n.First is not null && n.Second is not null
                && Check(n.First, depth + 1) && Check(n.Second, depth + 1);
        }
    }

    /// <summary>
    /// The form of <paramref name="layout"/> whose leaves are indexes into <paramref name="order"/> (the
    /// open tabs in the order they are written); ArgumentException when a pane is not in it.
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
    /// The layout that <paramref name="node"/> describes, leaf <c>n</c> being <c>items[n]</c> (ratios
    /// pulled into range). Null when <paramref name="node"/> is not <see cref="IsValid"/> for that many
    /// items, a leaf's item is null (the app could not restore that tab) or two leaves are the same
    /// item: the window is then not split.
    /// </summary>
    public static PaneLayout<T>? FromSetting<T>(PaneNodeSetting? node, IReadOnlyList<T?> items)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!IsValid(node, items.Count))
        {
            return null;
        }

        var leaves = new List<T>();
        PaneNode<T>? Of(PaneNodeSetting n)
        {
            if (n.Split is null)
            {
                var item = items[n.Pane!.Value];
                if (item is null || leaves.Any(l => ReferenceEquals(l, item)))
                {
                    return null;
                }

                leaves.Add(item);
                return new PaneLeaf<T>(item);
            }

            return Of(n.First!) is { } first && Of(n.Second!) is { } second
                ? new PaneSplit<T>(AxisOf(n.Split)!.Value, PaneRules.NormalizeRatio(n.Ratio ?? PaneRules.DefaultRatio), first, second)
                : null;
        }

        return Of(node!) is { } root ? new PaneLayout<T>(root) : null;
    }

    /// <summary>
    /// A copy of the valid (<see cref="IsValid"/>) <paramref name="node"/> with each leaf's index replaced
    /// by <paramref name="newIndex"/>'s entry for it, the split values in their written form and the
    /// ratios in range. Null when a leaf's entry is negative (its item was dropped).
    /// </summary>
    internal static PaneNodeSetting? Normalize(PaneNodeSetting node, IReadOnlyList<int> newIndex)
    {
        if (node.Split is null)
        {
            int index = newIndex[node.Pane!.Value];
            return index < 0 ? null : new PaneNodeSetting { Pane = index };
        }

        return Normalize(node.First!, newIndex) is { } first && Normalize(node.Second!, newIndex) is { } second
            ? new PaneNodeSetting
            {
                Split = ToSetting(AxisOf(node.Split)!.Value),
                Ratio = PaneRules.NormalizeRatio(node.Ratio ?? PaneRules.DefaultRatio),
                First = first,
                Second = second,
            }
            : null;
    }
}
