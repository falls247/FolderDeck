namespace FolderDeck.Core.Ordering;

/// <summary>How a tab orders its cards (one mode per tab, saved in settings.json).</summary>
public enum CardSortMode
{
    /// <summary>Top level: the drag &amp; drop order. Inside folders: folders first, then files, natural name order.</summary>
    Manual,
    NameAsc,
    NameDesc,
    ModifiedAsc,
    ModifiedDesc,

    /// <summary>By file size; folders have none and come after the files at a top level.</summary>
    SizeAsc,
    SizeDesc,

    /// <summary>By extension (not by the type name the details list shows, which arrives later).</summary>
    TypeAsc,
    TypeDesc,
}

/// <summary>What a sort mode other than <see cref="CardSortMode.Manual"/> sorts by: a column of the details list.</summary>
public enum SortColumn
{
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>Conversion between <see cref="CardSortMode"/> and its settings.json value.</summary>
public static class CardSortModes
{
    public const string Manual = "manual";
    public const string NameAsc = "name_asc";
    public const string NameDesc = "name_desc";
    public const string ModifiedAsc = "modified_asc";
    public const string ModifiedDesc = "modified_desc";
    public const string SizeAsc = "size_asc";
    public const string SizeDesc = "size_desc";
    public const string TypeAsc = "type_asc";
    public const string TypeDesc = "type_desc";

    /// <summary>Every mode in menu order.</summary>
    public static IReadOnlyList<CardSortMode> All { get; } =
    [
        CardSortMode.Manual,
        CardSortMode.NameAsc, CardSortMode.NameDesc,
        CardSortMode.ModifiedAsc, CardSortMode.ModifiedDesc,
        CardSortMode.SizeAsc, CardSortMode.SizeDesc,
        CardSortMode.TypeAsc, CardSortMode.TypeDesc,
    ];

    public static string ToSetting(CardSortMode mode) => mode switch
    {
        CardSortMode.NameAsc => NameAsc,
        CardSortMode.NameDesc => NameDesc,
        CardSortMode.ModifiedAsc => ModifiedAsc,
        CardSortMode.ModifiedDesc => ModifiedDesc,
        CardSortMode.SizeAsc => SizeAsc,
        CardSortMode.SizeDesc => SizeDesc,
        CardSortMode.TypeAsc => TypeAsc,
        CardSortMode.TypeDesc => TypeDesc,
        _ => Manual,
    };

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="CardSortMode.Manual"/>.</summary>
    public static CardSortMode FromSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        NameAsc => CardSortMode.NameAsc,
        NameDesc => CardSortMode.NameDesc,
        ModifiedAsc => CardSortMode.ModifiedAsc,
        ModifiedDesc => CardSortMode.ModifiedDesc,
        SizeAsc => CardSortMode.SizeAsc,
        SizeDesc => CardSortMode.SizeDesc,
        TypeAsc => CardSortMode.TypeAsc,
        TypeDesc => CardSortMode.TypeDesc,
        _ => CardSortMode.Manual,
    };

    public static bool IsByModified(CardSortMode mode) => mode is CardSortMode.ModifiedAsc or CardSortMode.ModifiedDesc;

    /// <summary>The column <paramref name="mode"/> sorts by, or null for <see cref="CardSortMode.Manual"/>.</summary>
    public static SortColumn? ColumnOf(CardSortMode mode) => mode switch
    {
        CardSortMode.NameAsc or CardSortMode.NameDesc => SortColumn.Name,
        CardSortMode.ModifiedAsc or CardSortMode.ModifiedDesc => SortColumn.Modified,
        CardSortMode.SizeAsc or CardSortMode.SizeDesc => SortColumn.Size,
        CardSortMode.TypeAsc or CardSortMode.TypeDesc => SortColumn.Type,
        _ => null,
    };

    public static bool IsDescending(CardSortMode mode) =>
        mode is CardSortMode.NameDesc or CardSortMode.ModifiedDesc or CardSortMode.SizeDesc or CardSortMode.TypeDesc;

    /// <summary>
    /// The mode a click on the header of <paramref name="column"/> leads to: the other direction when
    /// <paramref name="current"/> already sorts by that column, otherwise the column's first
    /// direction (名前 and 種類 start ascending, 更新日時 and サイズ start with the newest / largest).
    /// It never returns <see cref="CardSortMode.Manual"/>.
    /// </summary>
    public static CardSortMode Next(CardSortMode current, SortColumn column)
    {
        bool descending = ColumnOf(current) == column
            ? !IsDescending(current)
            : column is SortColumn.Modified or SortColumn.Size;
        return column switch
        {
            SortColumn.Modified => descending ? CardSortMode.ModifiedDesc : CardSortMode.ModifiedAsc,
            SortColumn.Type => descending ? CardSortMode.TypeDesc : CardSortMode.TypeAsc,
            SortColumn.Size => descending ? CardSortMode.SizeDesc : CardSortMode.SizeAsc,
            _ => descending ? CardSortMode.NameDesc : CardSortMode.NameAsc,
        };
    }
}
