using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Transfer;

/// <summary>What happens to one item: it is copied, or moved (the source disappears).</summary>
public enum TransferKind
{
    Copy,
    Move,
}

/// <summary>Why an item is copied (<see cref="None"/> for a move).</summary>
public enum CopyReason
{
    /// <summary>The item is moved.</summary>
    None,

    /// <summary>A copy was asked for (a copy on the clipboard).</summary>
    Requested,

    /// <summary>The source is on read-only media (optical disc, write-protected volume).</summary>
    ReadOnlyMedia,

    /// <summary>The source cannot be deleted, so a move could not remove it.</summary>
    NotDeletable,
}

/// <summary>Why an item (or, with nothing left, the whole request) is not transferred.</summary>
public enum TransferRefusal
{
    None,

    /// <summary>There is no destination folder (virtual list, no tab, not a path).</summary>
    NoTarget,

    /// <summary>The item is already in the destination folder.</summary>
    SameFolder,

    /// <summary>The destination is the item itself or lies inside it.</summary>
    IntoItself,

    /// <summary>The source is not a fully qualified file system path.</summary>
    NotAPath,

    /// <summary>The source is a drive or share root (<c>C:\</c>, <c>\\server\share</c>).</summary>
    Root,
}

/// <summary>Where a source lives, as far as the move-or-copy decision is concerned.</summary>
public enum SourceMedia
{
    Local,
    Network,
    ReadOnly,
}

/// <summary>A source path with the facts probed about it (media, whether it can be deleted).</summary>
public readonly record struct TransferSource(string Path, SourceMedia Media, bool CanDelete);

/// <summary>One item of a plan: the normalized source path and what is done with it.</summary>
/// <param name="RenameOnCollision">
/// The copy goes into the folder the source is already in, so it gets a new name ("x - コピー").
/// </param>
public readonly record struct TransferItem(string Path, TransferKind Kind, CopyReason Reason, bool RenameOnCollision);

/// <summary>What the user asked for, and the rules of the gesture (a paste or a drop on a folder).</summary>
/// <param name="Requested">Move or copy, before the per-item rules.</param>
/// <param name="AllowSameFolderCopy">A copy into the source's own folder is allowed (it is renamed).</param>
public sealed record TransferOptions(TransferKind Requested, bool AllowSameFolderCopy)
{
    /// <summary>
    /// A paste: the clipboard's kind (cut = move), as Explorer does it. A cut from the network is
    /// really moved, as in Explorer.
    /// </summary>
    public static TransferOptions Paste(TransferKind clip) => new(clip, AllowSameFolderCopy: true);

    /// <summary>
    /// A drop on a folder: the kind <see cref="FileDrag.DropEffectFor"/> chose (modifier keys,
    /// volume). Nothing is copied into the folder it is already in (no renamed copy, unlike a
    /// paste), except a Ctrl drop on the shown folder's list (<paramref name="copyInPlace"/>, see
    /// <see cref="FileDrag.CopiesInPlace"/>), which duplicates as a paste does. Read-only media and
    /// undeletable sources are still copied when a move is asked for.
    /// </summary>
    public static TransferOptions Drop(TransferKind kind, bool copyInPlace = false) =>
        new(kind, AllowSameFolderCopy: copyInPlace && kind == TransferKind.Copy);
}

/// <summary>The outcome of <see cref="TransferRules.Decide"/>: what to run and what was left out.</summary>
/// <param name="Items">Items to transfer, in source order, with normalized paths.</param>
/// <param name="Skipped">Sources that are not transferred, with the reason.</param>
public sealed record TransferPlan(
    IReadOnlyList<TransferItem> Items,
    IReadOnlyList<(string Path, TransferRefusal Why)> Skipped)
{
    /// <summary>
    /// <see cref="TransferRefusal.None"/> while there is something to transfer; otherwise the reason
    /// of the first skipped source (<see cref="TransferRefusal.NotAPath"/> when there was no source
    /// at all). A caller runs the transfer only when this is None.
    /// </summary>
    public TransferRefusal Refusal =>
        Items.Count > 0 ? TransferRefusal.None
        : Skipped.Count > 0 ? Skipped[0].Why
        : TransferRefusal.NotAPath;

    /// <summary>Number of items that are moved.</summary>
    public int MoveCount
    {
        get
        {
            int moves = 0;
            foreach (TransferItem item in Items)
            {
                if (item.Kind == TransferKind.Move)
                {
                    moves++;
                }
            }

            return moves;
        }
    }

    /// <summary>Number of items that are copied.</summary>
    public int CopyCount => Items.Count - MoveCount;
}

/// <summary>
/// Decides, from strings and probed facts only (no file system access), whether sources may be
/// transferred into a folder and whether each one is moved or copied. A paste and a drop on a
/// folder go through <see cref="Decide"/> (the app has no other way to copy or move files). Paths are normalized with <see cref="PathTools"/> and
/// compared case-insensitively. Links, junctions and 8.3 short names are not resolved: two
/// different spellings of one folder are different paths here. The same-folder and into-itself
/// checks therefore hold only when sources and target use the same spelling: a caller whose paths
/// may carry 8.3 short names (a file list from the clipboard, which this app itself writes with
/// short names for paths over MAX_PATH) must expand them to the long spelling first
/// (<c>GetLongPathName</c>; the app's clipboard read does).
/// </summary>
public static class TransferRules
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const string DeviceUncPrefix = @"\\.\UNC\";

    /// <summary>
    /// True for <c>\\server\share\…</c> and <c>\\?\UNC\server\share\…</c>. Only the string is
    /// looked at: a mapped drive letter is not recognized (that is the probe's job).
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3)
        {
            return false;
        }

        string p = path.Replace('/', '\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        if (p.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(DeviceUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "\\?\C:\…" and "\\.\…" are local device paths, not servers.
        bool device = (p[2] is '?' or '.') && (p.Length == 3 || p[3] == '\\');
        return !device && p[2] != '\\';
    }

    /// <summary>
    /// Both paths lie on one volume as far as the spelling tells: the same drive letter or
    /// <c>\\server\share</c>, case-insensitively, <c>\\?\</c> prefixes ignored. No I/O: SUBST
    /// drives, folder mount points and a mapped letter against the UNC spelling of the same share
    /// count as different volumes. False for anything that is not a fully qualified path.
    /// </summary>
    public static bool SameVolume(string? a, string? b)
    {
        if (!TryKey(a, out _, out string keyA) || !TryKey(b, out _, out string keyB))
        {
            return false;
        }

        string rootA = TrimEnd(Path.GetPathRoot(keyA) ?? string.Empty);
        string rootB = TrimEnd(Path.GetPathRoot(keyB) ?? string.Empty);
        return rootA.Length > 0 && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the path may be a transfer source at all: a fully qualified path that is not a
    /// drive or share root.
    /// </summary>
    public static bool IsTransferable(string? path) =>
        TryKey(path, out string normalized, out string key) && !IsRootPath(normalized, key);

    /// <summary>
    /// Whether one source may go into <paramref name="target"/>; media are not looked at. Checked in
    /// this order: no usable target, not a path, a root, the target is the item or inside it, the
    /// item is already in the target (a move always; a copy unless
    /// <paramref name="allowSameFolderCopy"/>).
    /// </summary>
    public static TransferRefusal Check(string path, string? target, TransferKind kind, bool allowSameFolderCopy)
    {
        if (!TryKey(target, out _, out string targetKey))
        {
            return TransferRefusal.NoTarget;
        }

        bool qualified = TryKey(path, out string normalized, out string key);
        return CheckCore(qualified, normalized, key, targetKey, kind, allowSameFolderCopy, out _);
    }

    /// <summary>
    /// Builds the plan for transferring <paramref name="sources"/> into <paramref name="target"/>.
    /// <list type="number">
    /// <item>Sources are normalized and deduplicated; a source inside another source is dropped
    /// silently (it travels with, or is refused with, that source).</item>
    /// <item>A source that is not a fully qualified path is skipped as NotAPath, a root as Root.</item>
    /// <item>A target that is the item or lies inside it: IntoItself (moves and copies).</item>
    /// <item>An item already in the target: SameFolder for a requested move; for a requested copy
    /// too, unless <see cref="TransferOptions.AllowSameFolderCopy"/>, which gives a renamed copy
    /// (a paste allows it, a drop does not).</item>
    /// <item>Kind per item: a requested copy stays a copy; read-only media and undeletable sources
    /// are copied; the rest is moved (network sources too).</item>
    /// </list>
    /// Without a usable target every source is skipped as NoTarget.
    /// </summary>
    public static TransferPlan Decide(IReadOnlyList<TransferSource> sources, string? target, TransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        bool hasTarget = TryKey(target, out _, out string targetKey);

        // 1. Normalize and dedupe (the first occurrence wins).
        var candidates = new List<Candidate>(sources.Count);
        var seen = new HashSet<string>(PathTools.Comparer);
        var containers = new HashSet<string>(PathTools.Comparer);
        foreach (TransferSource source in sources)
        {
            bool qualified = TryKey(source.Path, out string normalized, out string key);
            if (!seen.Add(key))
            {
                continue;
            }

            candidates.Add(new Candidate(source, normalized, key, qualified));
            if (qualified)
            {
                containers.Add(TrimEnd(key));
            }
        }

        var items = new List<TransferItem>();
        var skipped = new List<(string Path, TransferRefusal Why)>();
        foreach (Candidate candidate in candidates)
        {
            // 1. (continued) A source inside another source is not an item of its own.
            if (candidate.Qualified && LiesInsideAnother(candidate.Key, containers))
            {
                continue;
            }

            if (!hasTarget)
            {
                skipped.Add((candidate.Path, TransferRefusal.NoTarget));
                continue;
            }

            // 2. to 4.
            TransferRefusal why = CheckCore(
                candidate.Qualified, candidate.Path, candidate.Key, targetKey,
                options.Requested, options.AllowSameFolderCopy, out bool renameOnCollision);
            if (why != TransferRefusal.None)
            {
                skipped.Add((candidate.Path, why));
                continue;
            }

            // 5.
            (TransferKind kind, CopyReason reason) = KindOf(candidate.Source, candidate.Path, options);
            items.Add(new TransferItem(candidate.Path, kind, reason, renameOnCollision));
        }

        return new TransferPlan(items, skipped);
    }

    /// <summary>
    /// Normalizes a fully qualified path (<see cref="PathTools.Normalize"/>). False, with the input
    /// (or an empty string) in <paramref name="normalized"/>, for anything else. Environment
    /// variables are not expanded and quotes are not removed: this is a real path, not typed input.
    /// </summary>
    internal static bool TryNormalize(string? path, out string normalized)
    {
        normalized = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !PathTools.IsFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = PathTools.Normalize(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Steps 2 to 4 of <see cref="Decide"/> for one normalized source.</summary>
    private static TransferRefusal CheckCore(
        bool qualified, string path, string key, string targetKey,
        TransferKind kind, bool allowSameFolderCopy, out bool renameOnCollision)
    {
        renameOnCollision = false;

        // 2. Roots come before the prefix test: TrimTrailingSeparators keeps a root's separator.
        if (!qualified)
        {
            return TransferRefusal.NotAPath;
        }

        if (IsRootPath(path, key))
        {
            return TransferRefusal.Root;
        }

        // 3. The trailing separator in the prefix keeps "E:\ab" out of "E:\a".
        string item = TrimEnd(key);
        string destination = TrimEnd(targetKey);
        if (PathTools.Comparer.Equals(item, destination)
            || destination.StartsWith(item + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return TransferRefusal.IntoItself;
        }

        // 4.
        string parent = PathTools.ParentOf(key);
        if (parent.Length == 0)
        {
            return TransferRefusal.NotAPath; // no folder to take it from
        }

        if (PathTools.Comparer.Equals(TrimEnd(parent), destination))
        {
            if (kind == TransferKind.Move || !allowSameFolderCopy)
            {
                return TransferRefusal.SameFolder;
            }

            renameOnCollision = true;
        }

        return TransferRefusal.None;
    }

    /// <summary>Step 5 of <see cref="Decide"/>.</summary>
    private static (TransferKind Kind, CopyReason Reason) KindOf(TransferSource source, string path, TransferOptions options)
    {
        if (options.Requested == TransferKind.Copy)
        {
            return (TransferKind.Copy, CopyReason.Requested);
        }

        if (source.Media == SourceMedia.ReadOnly)
        {
            return (TransferKind.Copy, CopyReason.ReadOnlyMedia);
        }

        if (!source.CanDelete)
        {
            return (TransferKind.Copy, CopyReason.NotDeletable);
        }

        return (TransferKind.Move, CopyReason.None);
    }

    /// <summary>
    /// Normalized path plus the key it is compared by: the same path without the <c>\\?\</c> /
    /// <c>\\?\UNC\</c> prefix, so that <c>\\?\E:\a</c> and <c>E:\a</c> are one folder.
    /// </summary>
    private static bool TryKey(string? path, out string normalized, out string key)
    {
        if (!TryNormalize(path, out normalized))
        {
            key = normalized;
            return false;
        }

        string stripped;
        if (normalized.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            stripped = @"\\" + normalized[ExtendedUncPrefix.Length..];
        }
        else if (normalized.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            stripped = normalized[ExtendedPrefix.Length..];
        }
        else
        {
            key = normalized;
            return true;
        }

        key = TryNormalize(stripped, out string plain) ? plain : stripped;
        return true;
    }

    private static bool IsRootPath(string normalized, string key) =>
        PathTools.IsRoot(normalized) || PathTools.IsRoot(key);

    /// <summary>True when an ancestor folder of <paramref name="key"/> is itself a source.</summary>
    private static bool LiesInsideAnother(string key, HashSet<string> containers)
    {
        string k = TrimEnd(key);
        for (int i = k.LastIndexOf('\\'); i > 0; i = k.LastIndexOf('\\', i - 1))
        {
            if (containers.Contains(k[..i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimEnd(string path) => path.TrimEnd('\\', '/');

    private readonly record struct Candidate(TransferSource Source, string Path, string Key, bool Qualified);
}

/// <summary>How a transfer ended, for the message shown afterwards.</summary>
public enum TransferOutcome
{
    /// <summary>Every requested item was transferred.</summary>
    Completed,

    /// <summary>Some items were transferred, some were not (skipped, failed or canceled midway).</summary>
    Partial,

    /// <summary>Nothing was transferred because the user canceled or skipped everything.</summary>
    Canceled,

    /// <summary>Nothing was transferred because of an error.</summary>
    Failed,
}

/// <summary>Turns the counters of a finished transfer into a <see cref="TransferOutcome"/>.</summary>
public static class TransferOutcomes
{
    /// <summary>
    /// Classifies a finished transfer from its counters. All done is Completed whatever else was
    /// reported; some done is Partial; nothing done is Canceled when the user aborted, Failed when
    /// there is an error, and Canceled otherwise (every item was skipped in the shell's dialogs).
    /// With nothing requested: Failed when there is an error, else Canceled when aborted, else
    /// Completed.
    /// </summary>
    public static TransferOutcome Classify(int requested, int done, bool aborted, string? error)
    {
        if (requested <= 0)
        {
            return error is not null ? TransferOutcome.Failed
                : aborted ? TransferOutcome.Canceled
                : TransferOutcome.Completed;
        }

        if (done >= requested)
        {
            return TransferOutcome.Completed;
        }

        if (done > 0)
        {
            return TransferOutcome.Partial;
        }

        if (aborted)
        {
            return TransferOutcome.Canceled;
        }

        return error is not null ? TransferOutcome.Failed : TransferOutcome.Canceled;
    }
}

/// <summary>Groups the targets of the native shell menu, which is built per parent folder.</summary>
public static class ShellMenuTargets
{
    /// <summary>
    /// The one parent folder all paths share, with the normalized paths (each once, in order).
    /// Null when the list is empty, the paths have several parents, or one of them is a root or not
    /// a fully qualified path.
    /// </summary>
    public static (string Parent, IReadOnlyList<string> Paths)? TryGroup(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return null;
        }

        string? parent = null;
        var seen = new HashSet<string>(PathTools.Comparer);
        var list = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (!TransferRules.TryNormalize(path, out string normalized) || PathTools.IsRoot(normalized))
            {
                return null;
            }

            string itemParent = PathTools.ParentOf(normalized);
            if (itemParent.Length == 0)
            {
                return null;
            }

            if (parent is null)
            {
                parent = itemParent;
            }
            else if (!PathTools.PathEquals(parent, itemParent))
            {
                return null;
            }

            if (seen.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        if (parent is null)
        {
            return null;
        }

        return (parent, (IReadOnlyList<string>)list);
    }
}
