namespace FolderDeck.Core.Layout;

/// <summary>
/// One extra column of the details view: a Windows property shown as Explorer shows it.
/// <paramref name="Id"/> is the key in settings.json (<c>details_extra_columns</c>, <c>details_columns</c>),
/// <paramref name="CanonicalName"/> the property system's name, <paramref name="Title"/> the header
/// until Windows' own (localized) name is read.
/// </summary>
public sealed record DetailsPropertyColumn(string Id, string CanonicalName, string Title, double DefaultWidth, bool AlignRight = false);

/// <summary>
/// The extra columns of the details view, as in Explorer's column chooser (the header's right-click
/// menu): hidden by default, shown in this fixed order after サイズ, each with its own stored width.
/// The values come from the item's property store (the App's ShellPropertyCache); the list is not
/// sorted by them (they arrive late, and a sort key that arrives late moves the rows).
/// </summary>
public static class DetailsPropertyColumns
{
    public static IReadOnlyList<DetailsPropertyColumn> All { get; } =
    [
        new("date_taken", "System.Photo.DateTaken", "撮影日時", 140),
        new("authors", "System.Author", "作成者", 150),
        new("dimensions", "System.Image.Dimensions", "大きさ", 100),
        new("camera_maker", "System.Photo.CameraManufacturer", "カメラの製造元", 130),
        new("camera_model", "System.Photo.CameraModel", "カメラのモデル", 130),
        new("created", "System.DateCreated", "作成日時", 140),
        new("title", "System.Title", "タイトル", 150),
        new("tags", "System.Keywords", "タグ", 130),
        new("duration", "System.Media.Duration", "長さ", 80, AlignRight: true),
        new("rating", "System.Rating", "評価", 90),
    ];

    public static DetailsPropertyColumn? ById(string? id)
    {
        string key = id?.Trim() ?? string.Empty;
        return All.FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The shown extra columns as stored: known ids only (case and spaces ignored), each once, in the
    /// order of <see cref="All"/> (the columns are not reordered, so the header and the rows share it).
    /// </summary>
    public static List<string> NormalizeShown(IEnumerable<string?>? ids)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? id in ids ?? [])
        {
            if (ById(id) is { } column)
            {
                wanted.Add(column.Id);
            }
        }

        return All.Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToList();
    }
}
