namespace FolderDeck.Core.Selection;

/// <summary>
/// Enter on the selection (Explorer's rule): one usable item opens as a click on it; several open
/// every file, then every folder on its own; more than <see cref="ConfirmAbove"/> ask first.
/// </summary>
public static class OpenPlan
{
    /// <summary>Explorer asks before opening more than this many items at once (MultipleInvokePromptMinimum).</summary>
    public const int ConfirmAbove = 15;

    /// <summary>
    /// What Enter opens: <see cref="Single"/> when one item is usable (then the lists are empty),
    /// otherwise the files and the folders, each in display order.
    /// </summary>
    public sealed record Plan<T>(T? Single, IReadOnlyList<T> Files, IReadOnlyList<T> Folders) where T : class
    {
        public int Count => Single is null ? Files.Count + Folders.Count : 1;

        public bool IsEmpty => Count == 0;

        public bool NeedsConfirm => Count > ConfirmAbove;
    }

    /// <summary><paramref name="selected"/> in display order; items that <paramref name="canOpen"/> rejects are left out.</summary>
    public static Plan<T> Of<T>(IReadOnlyList<T> selected, Func<T, bool> isFolder, Func<T, bool> canOpen) where T : class
    {
        var usable = selected.Where(canOpen).ToList();
        if (usable.Count == 1)
        {
            return new Plan<T>(usable[0], [], []);
        }

        return new Plan<T>(null, usable.Where(x => !isFolder(x)).ToList(), usable.Where(isFolder).ToList());
    }
}
