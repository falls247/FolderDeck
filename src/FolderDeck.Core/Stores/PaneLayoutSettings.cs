using System.Text.Json;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

/// <summary>
/// Converts a <see cref="PaneLayout{T}"/> to and from its JSON form (<see cref="PaneNodeSetting"/>) and
/// holds the checks of that form. Three kinds of trees use it: a pane group of settings.json
/// (<see cref="AppSettings.PaneGroups"/>: leaves <c>pane</c> = an index into <see cref="AppSettings.OpenTabs"/>,
/// or <c>empty</c>), a registered pane (registered_panes.json: leaves <c>tab</c> = the tab itself, or
/// <c>empty</c>) and the old <see cref="AppSettings.SplitLayout"/> (leaves <c>pane</c> only, read for
/// migration). The shape rules are the same for all: 2 to <see cref="PaneRules.MaxPanes"/> leaves, every
/// split has a known <see cref="PaneNodeSetting.Split"/>, both children and nothing of a leaf.
/// </summary>
public static class PaneLayoutSettings
{
    public const string Columns = "columns";
    public const string Rows = "rows";

    /// <summary>What a leaf holds: exactly one of <c>pane</c>, <c>tab</c> or <c>empty: true</c>.</summary>
    private enum LeafKind
    {
        Invalid,
        Pane,
        Tab,
        Empty,
    }

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
    /// tabs) in the form of the old <see cref="AppSettings.SplitLayout"/>: 2 to <see cref="PaneRules.MaxPanes"/>
    /// leaves, every leaf has an index in range and no children, no index twice. Items without a leaf are
    /// fine (tabs that are not shown).
    /// </summary>
    public static bool IsValid(PaneNodeSetting? node, int itemCount)
    {
        var used = new HashSet<int>();
        return itemCount >= 2 && IsTree(node, n => n.Pane is int index && index >= 0 && index < itemCount && used.Add(index));
    }

    /// <summary>
    /// Whether <paramref name="node"/> is a pane group of settings.json for <paramref name="itemCount"/>
    /// open tabs: 2 to <see cref="PaneRules.MaxPanes"/> leaves, each an index in range (no index twice) or
    /// an empty pane, and at least one of them an index (a group of empty panes only is no group).
    /// </summary>
    public static bool IsValidGroup(PaneNodeSetting? node, int itemCount)
    {
        var used = new HashSet<int>();
        return IsTree(node, n => KindOf(n) switch
        {
            LeafKind.Pane => n.Pane!.Value >= 0 && n.Pane.Value < itemCount && used.Add(n.Pane.Value),
            LeafKind.Empty => true,
            _ => false,
        }) && used.Count > 0;
    }

    /// <summary>
    /// Whether <paramref name="node"/> is a registered pane's layout: 2 to <see cref="PaneRules.MaxPanes"/>
    /// leaves, each a tab (<see cref="IsValidSavedTab"/>; 最近の履歴 and よく使う項目 at most once each, as
    /// on the tab strip) or an empty pane, and at least one of them a tab.
    /// </summary>
    public static bool IsValidSaved(PaneNodeSetting? node)
    {
        int tabs = 0;
        var fixedKinds = new HashSet<string>();
        return IsTree(node, Leaf) && tabs > 0;

        bool Leaf(PaneNodeSetting n)
        {
            switch (KindOf(n))
            {
                case LeafKind.Empty:
                    return true;
                case LeafKind.Tab when IsValidSavedTab(n.Tab!):
                    string kind = TabKindOf(n.Tab!.Kind)!;
                    tabs++;
                    return kind == AppSettings.TabFolder || fixedKinds.Add(kind);
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The form of <paramref name="layout"/> whose leaves <paramref name="leaf"/> writes (in reading order),
    /// the splits with their written axis and ratio.
    /// </summary>
    public static PaneNodeSetting ToSetting<T>(PaneLayout<T> layout, Func<T, PaneNodeSetting> leaf)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(leaf);

        PaneNodeSetting Of(PaneNode<T> node) => node switch
        {
            PaneLeaf<T> l => leaf(l.Pane) ?? throw new InvalidOperationException("The leaf writer returned null."),
            PaneSplit<T> split => new PaneNodeSetting { Split = ToSetting(split.Axis), Ratio = split.Ratio, First = Of(split.First), Second = Of(split.Second) },
            _ => throw new InvalidOperationException(),
        };

        return Of(layout.Root);
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
        return ToSetting(layout, pane =>
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (ReferenceEquals(order[i], pane))
                {
                    return new PaneNodeSetting { Pane = i };
                }
            }

            throw new ArgumentException("A pane of the layout is not in the order.", nameof(order));
        });
    }

    /// <summary>
    /// The layout that <paramref name="node"/> describes, each leaf being what <paramref name="leaf"/> makes
    /// of it (asked in reading order; ratios pulled into range). Null when the shape is wrong (see
    /// <see cref="PaneLayoutSettings"/>), when <paramref name="leaf"/> gives null for a leaf (it stops
    /// there: later leaves are not asked) or when two leaves give the same item.
    /// </summary>
    public static PaneLayout<T>? FromSetting<T>(PaneNodeSetting? node, Func<PaneNodeSetting, T?> leaf)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(leaf);
        if (!IsTree(node, _ => true))
        {
            return null;
        }

        var leaves = new List<T>();
        PaneNode<T>? Of(PaneNodeSetting n)
        {
            if (n.Split is null)
            {
                var item = leaf(n);
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
    /// The layout that <paramref name="node"/> describes, leaf <c>n</c> being <c>items[n]</c> (ratios
    /// pulled into range). Null when <paramref name="node"/> is not <see cref="IsValid"/> for that many
    /// items, a leaf's item is null (the app could not restore that tab) or two leaves are the same
    /// item: the window is then not split.
    /// </summary>
    public static PaneLayout<T>? FromSetting<T>(PaneNodeSetting? node, IReadOnlyList<T?> items)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        return IsValid(node, items.Count) ? FromSetting(node, n => items[n.Pane!.Value]) : null;
    }

    /// <summary>
    /// Whether <paramref name="tab"/> can be a leaf of a registered pane: a known kind (none: a folder tab),
    /// a root for a folder tab and a path that does not climb out of its root.
    /// </summary>
    private static bool IsValidSavedTab(OpenTabSetting tab) =>
        TabKindOf(tab.Kind) is { } kind
        && (kind != AppSettings.TabFolder || !string.IsNullOrWhiteSpace(tab.Root))
        && PathTools.IsSafeRel(tab.Path);

    /// <summary>The written kind of a tab (case and spaces ignored, none: a folder tab); null when unknown.</summary>
    private static string? TabKindOf(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or AppSettings.TabFolder => AppSettings.TabFolder,
        AppSettings.TabRecent => AppSettings.TabRecent,
        AppSettings.TabFrequent => AppSettings.TabFrequent,
        _ => null,
    };

    private static LeafKind KindOf(PaneNodeSetting leaf)
    {
        bool empty = leaf.Empty == true;
        int held = (leaf.Pane is null ? 0 : 1) + (leaf.Tab is null ? 0 : 1) + (empty ? 1 : 0);
        return held != 1 ? LeafKind.Invalid
            : leaf.Pane is not null ? LeafKind.Pane
            : leaf.Tab is not null ? LeafKind.Tab
            : LeafKind.Empty;
    }

    /// <summary>
    /// Whether <paramref name="node"/> has the shape every tree needs (see <see cref="PaneLayoutSettings"/>)
    /// and every leaf passes <paramref name="leafOk"/> (asked in reading order, never for more than
    /// <see cref="PaneRules.MaxPanes"/> leaves).
    /// </summary>
    private static bool IsTree(PaneNodeSetting? node, Func<PaneNodeSetting, bool> leafOk)
    {
        if (node is null)
        {
            return false;
        }

        int leaves = 0;
        return Check(node, 0) && leaves >= 2;

        bool Check(PaneNodeSetting n, int depth)
        {
            if (depth >= PaneRules.MaxPanes)
            {
                return false; // a tree of MaxPanes leaves is never this deep
            }

            if (n.Split is null)
            {
                return n.First is null && n.Second is null && ++leaves <= PaneRules.MaxPanes && leafOk(n);
            }

            return AxisOf(n.Split) is not null && n.Pane is null && n.Tab is null && n.Empty is null or false
                && n.First is not null && n.Second is not null
                && Check(n.First, depth + 1) && Check(n.Second, depth + 1);
        }
    }

    /// <summary>The leaves of a tree of the right shape, in reading order.</summary>
    internal static List<PaneNodeSetting> LeavesOf(PaneNodeSetting node)
    {
        var leaves = new List<PaneNodeSetting>();
        void Collect(PaneNodeSetting n)
        {
            if (n.Split is null)
            {
                leaves.Add(n);
                return;
            }

            Collect(n.First!);
            Collect(n.Second!);
        }

        Collect(node);
        return leaves;
    }

    /// <summary>
    /// Whether <paramref name="node"/> has the shape of a pane group whatever its indexes point at: every
    /// leaf an index or an empty pane. <see cref="AppSettings.Normalize"/> keeps such a group (its leaves
    /// whose tab is gone become empty panes) until no tab of it is left.
    /// </summary>
    internal static bool IsGroupShape(PaneNodeSetting? node) =>
        IsTree(node, n => KindOf(n) is LeafKind.Pane or LeafKind.Empty);

    /// <summary>
    /// A pane group (<see cref="IsGroupShape"/>) after the open tabs were normalized: each leaf's index is
    /// replaced by <paramref name="newIndex"/>'s entry for it; a leaf whose tab was dropped (negative or
    /// out of range) or whose tab is in <paramref name="used"/> already (an earlier group or leaf shows it)
    /// becomes an empty pane, the others are added to <paramref name="used"/>. The splits are in their
    /// written form, the ratios in range. Null when the shape is wrong or no leaf shows a tab.
    /// </summary>
    internal static PaneNodeSetting? NormalizeGroup(PaneNodeSetting? node, IReadOnlyList<int> newIndex, ISet<int> used)
    {
        if (!IsGroupShape(node))
        {
            return null;
        }

        bool shows = false;
        var normalized = Canonical(node!, leaf =>
        {
            if (leaf.Pane is int old && old >= 0 && old < newIndex.Count && newIndex[old] is int now && now >= 0 && used.Add(now))
            {
                shows = true;
                return new PaneNodeSetting { Pane = now };
            }

            return PaneNodeSetting.EmptyLeaf();
        });
        return shows ? normalized : null;
    }

    /// <summary>
    /// A copy of a registered pane's layout in its written form (kinds, splits and ratios; a tab without
    /// a root shows its top level), or null when it is not <see cref="IsValidSaved"/>.
    /// </summary>
    internal static PaneNodeSetting? NormalizeSaved(PaneNodeSetting? node)
    {
        if (!IsValidSaved(node))
        {
            return null;
        }

        return Canonical(node!, leaf =>
        {
            if (leaf.Tab is not { } tab)
            {
                return PaneNodeSetting.EmptyLeaf();
            }

            bool blankRoot = string.IsNullOrWhiteSpace(tab.Root);
            return new PaneNodeSetting
            {
                Tab = new OpenTabSetting
                {
                    Kind = TabKindOf(tab.Kind),
                    Root = blankRoot ? string.Empty : tab.Root,
                    Path = blankRoot ? string.Empty : tab.Path ?? string.Empty,
                    FileId = blankRoot || string.IsNullOrWhiteSpace(tab.FileId) ? null : tab.FileId,
                    CardShape = tab.CardShape is null ? null : CardShapes.ToSetting(CardShapes.FromSetting(tab.CardShape)),
                    SortMode = tab.SortMode is null ? null : CardSortModes.ToSetting(CardSortModes.FromSetting(tab.SortMode)),
                },
            };
        });
    }

    /// <summary>Whether two trees are written the same (deep comparison of every member).</summary>
    internal static bool SameContent(PaneNodeSetting? a, PaneNodeSetting? b) =>
        JsonSerializer.Serialize(a, JsonFile.Options) == JsonSerializer.Serialize(b, JsonFile.Options);

    /// <summary>A copy of a tree of the right shape with the splits in their written form and the ratios in range.</summary>
    private static PaneNodeSetting Canonical(PaneNodeSetting n, Func<PaneNodeSetting, PaneNodeSetting> leaf) =>
        n.Split is null
            ? leaf(n)
            : new PaneNodeSetting
            {
                Split = ToSetting(AxisOf(n.Split)!.Value),
                Ratio = PaneRules.NormalizeRatio(n.Ratio ?? PaneRules.DefaultRatio),
                First = Canonical(n.First!, leaf),
                Second = Canonical(n.Second!, leaf),
            };
}
