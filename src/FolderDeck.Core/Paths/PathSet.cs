namespace FolderDeck.Core.Paths;

/// <summary>
/// Case-insensitive set of normalized paths (pins and the like). A lookup that misses retries with
/// the normalized form, so differently written paths still match.
/// </summary>
public sealed class PathSet
{
    private readonly HashSet<string> _paths = new(PathTools.Comparer);

    public PathSet(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!string.IsNullOrEmpty(path))
            {
                _paths.Add(PathTools.NormalizeOrSelf(path));
            }
        }
    }

    public int Count => _paths.Count;

    public bool Contains(string? path)
    {
        if (_paths.Count == 0 || string.IsNullOrEmpty(path))
        {
            return false;
        }

        return _paths.Contains(path) || _paths.Contains(PathTools.NormalizeOrSelf(path));
    }
}
