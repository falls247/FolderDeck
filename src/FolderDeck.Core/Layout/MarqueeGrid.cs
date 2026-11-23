namespace FolderDeck.Core.Layout;

/// <summary>A rectangle in the content coordinates of an item list (no WPF types in Core).</summary>
public readonly record struct MarqueeRect(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The rectangle between two corners, in any order.</summary>
    public static MarqueeRect Between(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}

/// <summary>
/// The cells of a list laid out row by row: <see cref="Columns"/> cells per row, <see cref="Count"/>
/// cells in all (the last row may be partial). Cell (r, c) covers
/// [c·PitchX, c·PitchX + CellWidth) × [r·PitchY, r·PitchY + CellHeight); the pitch minus the cell
/// size is the gap between cells. The details list is one column whose cell is the whole row.
/// </summary>
public readonly record struct CellGrid(int Columns, int Count, double CellWidth, double CellHeight, double PitchX, double PitchY);

/// <summary>Rows and columns of a grid, both inclusive; <see cref="Empty"/> when nothing is covered.</summary>
public readonly record struct CellSpan(int FirstRow, int LastRow, int FirstColumn, int LastColumn)
{
    public static CellSpan Empty => new(0, -1, 0, -1);

    public bool IsEmpty => FirstRow > LastRow || FirstColumn > LastColumn;
}

/// <summary>
/// The geometry of a marquee (rubber-band) selection: which cells a rectangle touches, computed
/// from the layout alone, so that items scrolled out of a virtualized list are found too.
/// </summary>
public static class MarqueeGrid
{
    /// <summary>
    /// The rows and columns whose cells overlap <paramref name="rect"/> (an edge that only touches
    /// a cell does not count; the gaps between cells select nothing). <see cref="CellSpan.Empty"/>
    /// for an empty grid, a non-positive or non-finite size, or a rectangle beside the cells.
    /// </summary>
    public static CellSpan Span(MarqueeRect rect, CellGrid grid)
    {
        if (grid.Columns < 1 || grid.Count <= 0
            || !Positive(grid.CellWidth) || !Positive(grid.CellHeight) || !Positive(grid.PitchX) || !Positive(grid.PitchY)
            || !Finite(rect.Left) || !Finite(rect.Top) || !Finite(rect.Right) || !Finite(rect.Bottom))
        {
            return CellSpan.Empty;
        }

        int rowCount = (grid.Count + grid.Columns - 1) / grid.Columns;
        (int firstRow, int lastRow) = Range(rect.Top, rect.Bottom, grid.CellHeight, grid.PitchY, rowCount);
        (int firstColumn, int lastColumn) = Range(rect.Left, rect.Right, grid.CellWidth, grid.PitchX, grid.Columns);
        var span = new CellSpan(firstRow, lastRow, firstColumn, lastColumn);
        return span.IsEmpty ? CellSpan.Empty : span;
    }

    /// <summary>The item indices of <paramref name="span"/> in display order (row by row), without the missing cells of a partial last row.</summary>
    public static IEnumerable<int> Indices(CellSpan span, int columns, int count)
    {
        if (span.IsEmpty || columns < 1)
        {
            yield break;
        }

        for (int r = span.FirstRow; r <= span.LastRow; r++)
        {
            for (int c = span.FirstColumn; c <= span.LastColumn; c++)
            {
                int index = r * columns + c;
                if (index >= count)
                {
                    break;
                }

                yield return index;
            }
        }
    }

    // The first index whose cell ends after `low`, and the last whose cell starts before `high`.
    // Only the far side is clamped: clamping the first index down to count - 1 would let a
    // rectangle below the content pick the last row.
    private static (int First, int Last) Range(double low, double high, double size, double pitch, int count)
    {
        double first = Math.Floor((low - size) / pitch) + 1;
        double last = Math.Ceiling(high / pitch) - 1;
        int f = first < 0 ? 0 : first > int.MaxValue ? int.MaxValue : (int)first;
        int l = last >= count ? count - 1 : last < -1 ? -1 : (int)last;
        return (f, l);
    }

    private static bool Positive(double value) => value > 0 && double.IsFinite(value);

    private static bool Finite(double value) => double.IsFinite(value);
}
