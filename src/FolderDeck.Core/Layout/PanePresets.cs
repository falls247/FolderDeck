namespace FolderDeck.Core.Layout;

/// <summary>
/// The pane layouts offered while a tab is dragged to the top of the panes (as Windows 11 offers its
/// snap layouts when a window is dragged to the top of the screen). Each has a fixed number of zones,
/// numbered in reading order (<see cref="PaneLayout{T}.Panes"/>); <see cref="Build"/> puts one pane in each.
/// </summary>
public static class PanePresets
{
    // 左右 2, 上下 2, 左右 3, 左 + 右の上下, 左の上下 + 右, 2 × 2
    private static readonly int[] s_zones = [2, 2, 3, 3, 3, 4];

    public static int Count => s_zones.Length;

    /// <summary>How many panes <paramref name="preset"/> has (never more than <see cref="PaneRules.MaxPanes"/>).</summary>
    public static int ZoneCount(int preset) => s_zones[preset];

    /// <summary>
    /// The layout of <paramref name="preset"/> with <paramref name="panes"/> in its zones, in reading
    /// order; equal shares (the three columns a third each).
    /// </summary>
    public static PaneLayout<T> Build<T>(int preset, IReadOnlyList<T> panes)
        where T : class
    {
        if ((uint)preset >= (uint)s_zones.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (panes.Count != s_zones[preset])
        {
            throw new ArgumentException($"The preset has {s_zones[preset]} zones.", nameof(panes));
        }

        PaneNode<T> L(int i) => new PaneLeaf<T>(panes[i]);
        static PaneNode<T> S(SplitAxis axis, double ratio, PaneNode<T> first, PaneNode<T> second) => new PaneSplit<T>(axis, ratio, first, second);
        const SplitAxis C = SplitAxis.Columns, R = SplitAxis.Rows;
        var root = preset switch
        {
            0 => S(C, 0.5, L(0), L(1)),
            1 => S(R, 0.5, L(0), L(1)),
            2 => S(C, 1.0 / 3, L(0), S(C, 0.5, L(1), L(2))),
            3 => S(C, 0.5, L(0), S(R, 0.5, L(1), L(2))),
            4 => S(C, 0.5, S(R, 0.5, L(0), L(1)), L(2)),
            _ => S(R, 0.5, S(C, 0.5, L(0), L(1)), S(C, 0.5, L(2), L(3))),
        };
        return new PaneLayout<T>(root);
    }

    /// <summary>Where each zone of <paramref name="preset"/> is in the unit square, in reading order (the picture of the preset).</summary>
    public static IReadOnlyList<(double X, double Y, double Width, double Height)> ZonesOf(int preset) =>
        Build(preset, Enumerable.Range(0, ZoneCount(preset)).Select(_ => new object()).ToList())
            .Arrange()
            .Select(z => (z.X, z.Y, z.Width, z.Height))
            .ToList();
}
