using System.Runtime.InteropServices;
using System.Windows;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Transfer;

namespace FolderDeck.Shell;

/// <summary>Where a drop on a folder of the app landed (recorded by the window's Drop; the transfer starts once the drag has ended).</summary>
public readonly record struct InAppDrop(string Folder, TransferKind Kind);

/// <summary>
/// One drag the app started: the data, the long-path spelling of what it carries (the file drop
/// may hold 8.3 short names) and whether Move is offered. A drop on a folder of the app is only
/// recorded here (<see cref="Dropped"/>) and run by <c>MainViewModel.FinishDrag</c> after
/// <see cref="FileDragOut.Run"/> returned.
/// </summary>
public sealed class OutgoingDrag(DataObject data, IReadOnlyList<string> paths, bool moveAllowed, bool intoFolders)
{
    public DataObject Data { get; } = data;

    /// <summary>The dragged items, as the app lists them (long paths, display order).</summary>
    public IReadOnlyList<string> Paths { get; } = paths;

    /// <summary>Every item may be cut (an item of a folder): Move is offered, to the app's folders and to other apps.</summary>
    public bool MoveAllowed { get; } = moveAllowed;

    /// <summary>Dropped on a folder of the app it is moved or copied into it (false: a 登録フォルダ row, which only reorders).</summary>
    public bool IntoFolders { get; } = intoFolders;

    public DragDropEffects AllowedEffects => FileDragOut.EffectsFor(MoveAllowed);

    /// <summary>The drag really ran (<see cref="FileDragOut.Run"/> did not refuse it).</summary>
    public bool Ran { get; internal set; }

    /// <summary>Set by the window when the drag was dropped on one of the app's folders.</summary>
    public InAppDrop? Dropped { get; set; }
}

/// <summary>
/// Drags files and folders out of the app the way Explorer does: <see cref="DataFormats.FileDrop"/>
/// (string[], CF_HDROP), so PowerPoint inserts a dropped image as a picture and Explorer copies or
/// moves the file. Copy | Link are always offered, Move only when every item can be cut (an item of
/// a folder, see <see cref="EffectsFor"/>); the receiver decides (Explorer: move on the same drive,
/// copy elsewhere, Shift / Ctrl to change it) and does the move itself. Whatever the effect, the
/// app never deletes the sources after a drag. Released over a folder of the app, the drop is only
/// recorded (<see cref="OutgoingDrag.Dropped"/>) and moved or copied with <see cref="FileTransfer"/>
/// once the drag has ended. No "Preferred DropEffect" is put on the data.
/// </summary>
public static class FileDragOut
{
    /// <summary>The effects a drag offers: Copy | Link, plus Move when every item may be cut.</summary>
    public static DragDropEffects EffectsFor(bool moveAllowed) =>
        DragDropEffects.Copy | DragDropEffects.Link | (moveAllowed ? DragDropEffects.Move : DragDropEffects.None);

    /// <summary>
    /// The drag the app is running, null when none. <c>DoDragDrop</c> blocks the UI thread until it
    /// ends, so the app's own drop targets see it set.
    /// </summary>
    public static OutgoingDrag? Current { get; private set; }

    /// <summary>A drag started by the app is running.</summary>
    public static bool IsDragging => Current is not null;

    /// <summary>Only real file system paths can be dragged out.</summary>
    public static bool CanDrag(string? path) => FileDrag.IsDraggablePath(path);

    /// <summary>
    /// The data of one drag: the file drop (long paths in 8.3 form), the app's own marker and, for a
    /// top-level card, the reorder format. Null when nothing can be dragged.
    /// </summary>
    public static DataObject? CreateData(IReadOnlyList<string> paths, bool reorder)
    {
        string[] drop = FileDrag.DropList(paths, ShellInterop.GetShortPath);
        if (drop.Length == 0)
        {
            return null;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, drop);
        data.SetData(FileDrag.SourceFormat, FileDrag.SourceFormat);
        if (reorder)
        {
            data.SetData(FileDrag.ReorderFormat, FileDrag.ReorderFormat);
        }

        return data;
    }

    /// <summary>
    /// Runs the drag (blocks until the drop or Esc) with <paramref name="source"/> as the drag source:
    /// an element that stays alive for the whole drag (the window or the card grid, not a list row
    /// whose popup closes). Returns the effect the receiver chose (None: cancelled or refused; an
    /// optimized move by Explorer may also answer None or Copy). The caller hands the drag to
    /// <c>MainViewModel.FinishDrag</c> afterwards.
    /// </summary>
    public static DragDropEffects Run(DependencyObject source, OutgoingDrag drag, string origin)
    {
        if (IsDragging)
        {
            return DragDropEffects.None;
        }

        Current = drag;
        drag.Ran = true;
        long started = Environment.TickCount64;
        try
        {
            var effect = DragDrop.DoDragDrop(source, drag.Data, drag.AllowedEffects);
            string where = drag.Dropped is { } d ? $"into app {d.Kind}" : "out";
            AppLog.Info($"drag-out: {origin}, effect {effect}, move {(drag.MoveAllowed ? "offered" : "not offered")}, {where} ({Environment.TickCount64 - started} ms)");
            return effect;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            drag.Dropped = null; // nothing is moved after a failed drag
            AppLog.Warn($"drag-out: {origin} failed", ex);
            return DragDropEffects.None;
        }
        finally
        {
            Current = null;
        }
    }
}
