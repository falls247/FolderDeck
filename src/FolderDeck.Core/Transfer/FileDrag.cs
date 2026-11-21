using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Transfer;

/// <summary>
/// Rules for dragging files and folders out of the app (to Explorer, PowerPoint, …) and for
/// telling the app's own drags apart from drops coming from Explorer. The WPF side only builds the
/// data object and runs <c>DragDrop.DoDragDrop</c>.
/// </summary>
public static class FileDrag
{
    /// <summary>Private format put on every drag the app starts (cards, list rows, previews).</summary>
    public const string SourceFormat = "FolderDeck.DragSource";

    /// <summary>
    /// Private format of a top-level card that may also be dropped back into the grid to change
    /// its position (the same drag carries the file drop for other apps).
    /// </summary>
    public const string ReorderFormat = "FolderDeck.CardReorder";

    /// <summary>
    /// Private format of a 登録フォルダ row of the sidebar: dropped back on the sidebar it changes the
    /// row's position (the same drag carries the file drop for other apps when the folder exists).
    /// </summary>
    public const string SidebarReorderFormat = "FolderDeck.SidebarReorder";

    /// <summary>
    /// Only real file system paths are dragged (<c>E:\…</c>, <c>\\server\share\…</c>); anything
    /// else would mean nothing to the app that receives it.
    /// </summary>
    public static bool IsDraggablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && PathTools.IsFullyQualified(path);

    /// <summary>
    /// The <c>FileDrop</c> list (CF_HDROP, as Explorer sends it): draggable paths in order, each once
    /// (paths compare case-insensitively), and paths of 260 characters or more in their 8.3 short
    /// form when that fits (<see cref="PathTools.ForShell"/>), since many receivers read the list
    /// with MAX_PATH buffers.
    /// </summary>
    public static string[] DropList(IEnumerable<string?> paths, Func<string, string?> getShortPath)
    {
        var seen = new HashSet<string>(PathTools.Comparer);
        var list = new List<string>();
        foreach (string? path in paths)
        {
            if (IsDraggablePath(path) && seen.Add(path!))
            {
                list.Add(PathTools.ForShell(path!, getShortPath));
            }
        }

        return [.. list];
    }

    /// <summary>
    /// True when the drag was started by this app (it carries one of the private formats). Such a
    /// drag is never a folder registration, even when it is dropped back onto the window.
    /// </summary>
    public static bool IsFromThisApp(Func<string, bool> hasFormat) =>
        hasFormat(SourceFormat) || hasFormat(ReorderFormat) || hasFormat(SidebarReorderFormat);

    /// <summary>
    /// The pointer moved, with the button held, further than the system drag distance
    /// (<c>SystemParameters.MinimumHorizontalDragDistance</c> / <c>MinimumVerticalDragDistance</c>)
    /// on either axis: the press becomes a drag (and no longer a click).
    /// </summary>
    public static bool IsDragGesture(double dx, double dy, double minWidth, double minHeight) =>
        Math.Abs(dx) > minWidth || Math.Abs(dy) > minHeight;
}
