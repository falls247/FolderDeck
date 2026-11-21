using System.Runtime.InteropServices;
using System.Windows;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Transfer;

namespace FolderDeck.Shell;

/// <summary>
/// Drags files and folders out of the app the way Explorer does: <see cref="DataFormats.FileDrop"/>
/// (string[], CF_HDROP), so PowerPoint inserts a dropped image as a picture and Explorer copies the
/// file. Only <see cref="AllowedEffects"/> (Copy | Link) are offered, never Move: dropped on Explorer
/// on the same drive, a move would take the file away, and the app never deletes, moves or
/// overwrites files.
/// </summary>
public static class FileDragOut
{
    public const DragDropEffects AllowedEffects = DragDropEffects.Copy | DragDropEffects.Link;

    /// <summary>
    /// A drag started by the app is running. <c>DoDragDrop</c> blocks the UI thread until it ends,
    /// so the app's own drop targets (the window's folder registration) see this flag set.
    /// </summary>
    public static bool IsDragging { get; private set; }

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
    /// whose popup closes). Returns the effect the receiver chose (None: cancelled or refused).
    /// </summary>
    public static DragDropEffects Run(DependencyObject source, DataObject data, string origin)
    {
        if (IsDragging)
        {
            return DragDropEffects.None;
        }

        IsDragging = true;
        long started = Environment.TickCount64;
        try
        {
            var effect = DragDrop.DoDragDrop(source, data, AllowedEffects);
            AppLog.Info($"drag-out: {origin}, effect {effect} ({Environment.TickCount64 - started} ms)");
            return effect;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            AppLog.Warn($"drag-out: {origin} failed", ex);
            return DragDropEffects.None;
        }
        finally
        {
            IsDragging = false;
        }
    }
}
