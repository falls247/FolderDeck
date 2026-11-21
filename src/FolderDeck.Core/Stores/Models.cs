namespace FolderDeck.Core.Stores;

public enum ItemKind
{
    File,
    Folder,
}

public static class ItemKinds
{
    public static string ToJson(ItemKind kind) => kind == ItemKind.Folder ? "folder" : "file";

    public static ItemKind? FromJson(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "folder" => ItemKind.Folder,
        "file" => ItemKind.File,
        _ => null,
    };
}

public sealed record RegisteredFolder(string Id, string Path, string AddedAt);

/// <summary>One usage record (history.json). <see cref="RecentOpens"/> is newest first, at most 10.</summary>
public sealed record HistoryItem(
    string Path,
    ItemKind Kind,
    int OpenCount,
    DateTimeOffset LastOpenedAt,
    IReadOnlyList<DateTimeOffset> RecentOpens);

public sealed record PinItem(string Path, ItemKind Kind, DateTimeOffset PinnedAt);

public sealed record ExcludedItem(string Path, DateTimeOffset ExcludedAt);

// ---- on-disk shapes -------------------------------------------------------------------------

public sealed class RegisteredFoldersDocument
{
    public int Version { get; set; } = 1;

    public List<RegisteredFolderDto>? Folders { get; set; } = [];
}

public sealed class RegisteredFolderDto
{
    public string? Id { get; set; }

    public string? Path { get; set; }

    public string? AddedAt { get; set; }
}

public sealed class HistoryDocument
{
    public int Version { get; set; } = 1;

    public List<HistoryItemDto>? Items { get; set; } = [];
}

public sealed class HistoryItemDto
{
    public string? Path { get; set; }

    public string? Kind { get; set; }

    public int OpenCount { get; set; }

    public DateTimeOffset? LastOpenedAt { get; set; }

    public List<DateTimeOffset?>? RecentOpens { get; set; } = [];
}

public sealed class PinsDocument
{
    public int Version { get; set; } = 1;

    public List<PinItemDto>? Pins { get; set; } = [];
}

public sealed class PinItemDto
{
    public string? Path { get; set; }

    public string? Kind { get; set; }

    public DateTimeOffset? PinnedAt { get; set; }
}

public sealed class ExcludedDocument
{
    public int Version { get; set; } = 1;

    public List<ExcludedItemDto>? Excluded { get; set; } = [];
}

public sealed class ExcludedItemDto
{
    public string? Path { get; set; }

    public DateTimeOffset? ExcludedAt { get; set; }
}
