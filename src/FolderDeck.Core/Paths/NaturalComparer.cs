using Windows.Win32;

namespace FolderDeck.Core.Paths;

/// <summary>Explorer's sort order (<c>StrCmpLogicalW</c>): "2" sorts before "10", case-insensitive.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        return PInvoke.StrCmpLogical(x, y);
    }
}
