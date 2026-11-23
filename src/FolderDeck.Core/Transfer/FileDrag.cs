using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Transfer;

/// <summary>DROPEFFECT bits (the values of WPF's <c>DragDropEffects</c>; Core does not reference WPF).</summary>
[Flags]
public enum DropEffects
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

/// <summary>Modifier keys and buttons held during a drag (the App maps <c>DragDropKeyStates</c>).</summary>
[Flags]
public enum DropKeys
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    RightButton = 8,
}

/// <summary>
/// Rules for dragging files and folders out of the app (to Explorer, PowerPoint, …), for telling
/// the app's own drags apart from drops coming from Explorer, and for the effect of a drop on a
/// folder of the app (move or copy into it). The WPF side only builds the data object, runs
/// <c>DragDrop.DoDragDrop</c> and answers the drop events.
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
    /// drag is never a folder registration, even when it is dropped back onto the window (over a
    /// folder of the app it is a move or copy into that folder instead).
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

    /// <summary>
    /// The effect of dropping <paramref name="sources"/> on the folder <paramref name="target"/>,
    /// as Explorer decides it: no key moves within one volume (<see cref="TransferRules.SameVolume"/>)
    /// and copies across volumes and shares; Ctrl copies; Shift moves; Alt, Ctrl+Shift and the right
    /// button give None (the app makes no shortcuts and has no drop menu). A default move that
    /// <paramref name="allowed"/> does not include becomes a copy; a forced one is refused. None when
    /// the target is unusable, a source is the target or contains it, a source lies in the Recycle
    /// Bin, or no source could go there at all (already in it, a root, not a path). Strings only, no
    /// I/O: it runs on every DragOver on the UI thread. What really moves is decided after the drop
    /// by <see cref="TransferRules.Decide"/> with <see cref="TransferOptions.Drop"/>.
    /// </summary>
    public static DropEffects DropEffectFor(IReadOnlyList<string>? sources, string? target, DropKeys keys, DropEffects allowed)
    {
        if (sources is null || sources.Count == 0)
        {
            return DropEffects.None;
        }

        bool control = (keys & DropKeys.Control) != 0;
        bool shift = (keys & DropKeys.Shift) != 0;
        if ((keys & (DropKeys.Alt | DropKeys.RightButton)) != 0 || (control && shift))
        {
            return DropEffects.None;
        }

        TransferKind kind;
        if (control)
        {
            kind = TransferKind.Copy;
        }
        else if (shift)
        {
            kind = TransferKind.Move;
        }
        else
        {
            kind = TransferKind.Move;
            foreach (string source in sources)
            {
                if (!TransferRules.SameVolume(source, target))
                {
                    kind = TransferKind.Copy;
                    break;
                }
            }
        }

        bool any = false;
        foreach (string source in sources)
        {
            if (IsInRecycleBin(source))
            {
                return DropEffects.None;
            }

            switch (TransferRules.Check(source, target, kind, allowSameFolderCopy: false))
            {
                case TransferRefusal.None:
                    any = true;
                    break;
                case TransferRefusal.NoTarget:
                case TransferRefusal.IntoItself:
                    return DropEffects.None;
            }
        }

        if (!any)
        {
            return DropEffects.None;
        }

        DropEffects wanted = kind == TransferKind.Move ? DropEffects.Move : DropEffects.Copy;
        if ((allowed & wanted) != 0)
        {
            return wanted;
        }

        bool defaultMove = wanted == DropEffects.Move && !shift;
        return defaultMove && (allowed & DropEffects.Copy) != 0 ? DropEffects.Copy : DropEffects.None;
    }

    /// <summary>
    /// What a drop target answers in <c>Drop</c> once it accepted: Copy, never Move. The app runs
    /// the move itself, after the drop; answering Move would let the source delete the originals
    /// (a non-optimized move, see "Handling Shell Data Transfer Scenarios"), also those the user
    /// then skips in the shell's collision dialog.
    /// </summary>
    public static DropEffects DropResult(DropEffects chosen) =>
        chosen == DropEffects.None ? DropEffects.None : DropEffects.Copy;

    private static bool IsInRecycleBin(string? path) =>
        path is not null && path.Replace('/', '\\').Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase);
}
