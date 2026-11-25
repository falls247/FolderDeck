namespace FolderDeck.Core.Paths;

/// <summary>
/// Paths that follow a folder that was renamed or moved (<c>old</c> → <c>new</c>): what was at or
/// below the old path is at the same place below the new one. Pure string work on normalized paths
/// (compared like <see cref="PathTools.Comparer"/>); nothing is checked on disk.
/// </summary>
public static class PathRebase
{
    /// <summary>
    /// <paramref name="path"/> moved along with <paramref name="oldPath"/>, or null when it is not at
    /// or below it. A root never moves.
    /// </summary>
    public static string? Rebase(string path, string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath) || PathTools.IsRoot(oldPath))
        {
            return null;
        }

        string? rel = PathTools.RelativeTo(oldPath, path);
        if (rel is null)
        {
            return null;
        }

        return rel.Length == 0 ? PathTools.TrimTrailingSeparators(newPath) : PathTools.Combine(PathTools.TrimTrailingSeparators(newPath), rel);
    }

    /// <summary>
    /// A tab's location (its <paramref name="root"/> and the <paramref name="rel"/> path below it)
    /// after the move, or null when it is not affected. The root moves when it is at or below the
    /// old path; a folder below the root keeps the root when it is still below it, else it becomes
    /// the root itself. A top level (no root) is never affected.
    /// </summary>
    public static (string Root, string Rel)? RebaseLocation(string root, string rel, string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        if (Rebase(root, oldPath, newPath) is { } movedRoot)
        {
            return (movedRoot, rel);
        }

        string full = PathTools.Combine(root, rel);
        if (Rebase(full, oldPath, newPath) is not { } movedFull)
        {
            return null;
        }

        return PathTools.RelativeTo(root, movedFull) is { } movedRel ? (root, movedRel) : (movedFull, string.Empty);
    }

    /// <summary>
    /// The widest rename in place that explains <paramref name="oldPath"/> → <paramref name="newPath"/>:
    /// the trailing names both have in common are dropped while what is left differs only in its last
    /// name (<c>E:\A\B\C</c> → <c>E:\A2\B\C</c> gives <c>E:\A</c> → <c>E:\A2</c>). A move to another
    /// parent gives the two paths unchanged. Only a guess from the names: the caller checks on disk
    /// that the old folder is gone and the new one is there before it uses a wider pair.
    /// </summary>
    public static (string Old, string New) WidestRename(string oldPath, string newPath)
    {
        string o = PathTools.TrimTrailingSeparators(oldPath);
        string n = PathTools.TrimTrailingSeparators(newPath);
        if (PathTools.PathEquals(o, n))
        {
            return (oldPath, newPath);
        }

        while (!PathTools.IsRoot(o) && !PathTools.IsRoot(n)
            && PathTools.Comparer.Equals(PathTools.DisplayName(o), PathTools.DisplayName(n)))
        {
            string po = PathTools.ParentOf(o);
            string pn = PathTools.ParentOf(n);
            if (po.Length == 0 || pn.Length == 0 || PathTools.PathEquals(po, pn))
            {
                break;
            }

            o = po;
            n = pn;
        }

        bool renamedInPlace = !PathTools.IsRoot(o) && !PathTools.IsRoot(n)
            && !PathTools.Comparer.Equals(PathTools.DisplayName(o), PathTools.DisplayName(n))
            && PathTools.PathEquals(PathTools.ParentOf(o), PathTools.ParentOf(n));
        return renamedInPlace ? (o, n) : (oldPath, newPath);
    }
}
