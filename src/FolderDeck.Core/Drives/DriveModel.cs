using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Drives;

/// <summary>Kind of a drive shown in the PC section of the sidebar.</summary>
public enum DriveKind
{
    /// <summary>Internal disk.</summary>
    Fixed,

    /// <summary>USB memory, card reader and other removable media.</summary>
    Removable,

    /// <summary>CD / DVD / Blu-ray drive.</summary>
    Optical,

    /// <summary>Mapped network drive.</summary>
    Network,
}

/// <summary>
/// One drive. <paramref name="Root"/> is the drive root (<c>C:\</c>). <paramref name="Label"/> is the
/// volume label, <paramref name="RemotePath"/> the UNC path a network drive is mapped to
/// (<c>\\server\share</c>). <paramref name="IsReady"/> is null until the drive has been probed.
/// </summary>
public sealed record DriveEntry(string Root, DriveKind Kind, string? Label, string? RemotePath, bool? IsReady);

/// <summary>Which drives are listed, in what order and under what name. Strings only: no disk access.</summary>
public static class DriveRules
{
    public const string FixedName = "ローカル ディスク";
    public const string RemovableName = "リムーバブル ディスク";
    public const string OpticalName = "DVD ドライブ";
    public const string NetworkName = "ネットワーク ドライブ";

    /// <summary>
    /// Fixed and network drives are always listed (a disconnected network drive stays as a disabled row).
    /// Removable and optical drives are listed only once a probe has found media in them, so an empty
    /// card reader slot or an unprobed drive stays hidden.
    /// </summary>
    public static bool ShouldShow(DriveKind kind, bool? isReady) =>
        (kind != DriveKind.Removable && kind != DriveKind.Optical) || isReady == true;

    /// <summary>
    /// Name in Explorer's form: <c>ラベル (C:)</c>. An empty label becomes the name of the kind.
    /// A network drive with a remote path is <c>share (\\server) (Z:)</c>; without one it falls back
    /// to its label, then to the name of the kind.
    /// </summary>
    public static string DisplayName(DriveEntry e)
    {
        string name = e.Kind == DriveKind.Network ? NetworkDisplayName(e) : LabelOrDefault(e);
        string letter = LetterOf(e.Root);
        return letter.Length == 0 ? name : $"{name} ({letter})";
    }

    /// <summary>
    /// Sorted by drive letter (case-insensitive). When the same root appears more than once
    /// (<c>Z:\</c>, <c>z:</c>), the first entry is kept.
    /// </summary>
    public static IReadOnlyList<DriveEntry> Order(IEnumerable<DriveEntry> entries)
    {
        var seen = new HashSet<string>(PathTools.Comparer);
        var distinct = new List<DriveEntry>();
        foreach (var entry in entries)
        {
            if (seen.Add(LetterOf(entry.Root)))
                distinct.Add(entry);
        }
        // OrderBy is stable, so entries without a letter keep their incoming order among equals.
        return distinct.OrderBy(d => LetterOf(d.Root), PathTools.Comparer).ToList();
    }

    private static string LabelOrDefault(DriveEntry e)
    {
        string label = e.Label?.Trim() ?? "";
        if (label.Length > 0) return label;
        return e.Kind switch
        {
            DriveKind.Removable => RemovableName,
            DriveKind.Optical => OpticalName,
            DriveKind.Network => NetworkName,
            _ => FixedName,
        };
    }

    /// <summary><c>\\server\share</c> → <c>share (\\server)</c>; a deeper mapping keeps its last folder as the name.</summary>
    private static string NetworkDisplayName(DriveEntry e)
    {
        string remote = (e.RemotePath ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
        if (remote.Length == 0) return LabelOrDefault(e);

        int cut = remote.LastIndexOf('\\');
        // cut <= 1 means only the leading "\\" is left ("\\server"): there is no share to split off.
        if (cut <= 1) return remote;
        return $"{remote[(cut + 1)..]} ({remote[..cut]})";
    }

    /// <summary><c>c:\</c> → <c>C:</c>. A root that has no drive letter is returned without its trailing separators.</summary>
    private static string LetterOf(string? root)
    {
        string trimmed = (root ?? "").Trim().TrimEnd('\\', '/');
        if (trimmed.Length >= 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]))
            return $"{char.ToUpperInvariant(trimmed[0])}:";
        return trimmed;
    }
}
