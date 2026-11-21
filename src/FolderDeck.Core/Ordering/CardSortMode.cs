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
}

/// <summary>Conversion between <see cref="CardSortMode"/> and its settings.json value.</summary>
public static class CardSortModes
{
    public const string Manual = "manual";
    public const string NameAsc = "name_asc";
    public const string NameDesc = "name_desc";
    public const string ModifiedAsc = "modified_asc";
    public const string ModifiedDesc = "modified_desc";

    /// <summary>Every mode in menu order.</summary>
    public static IReadOnlyList<CardSortMode> All { get; } =
        [CardSortMode.Manual, CardSortMode.NameAsc, CardSortMode.NameDesc, CardSortMode.ModifiedAsc, CardSortMode.ModifiedDesc];

    public static string ToSetting(CardSortMode mode) => mode switch
    {
        CardSortMode.NameAsc => NameAsc,
        CardSortMode.NameDesc => NameDesc,
        CardSortMode.ModifiedAsc => ModifiedAsc,
        CardSortMode.ModifiedDesc => ModifiedDesc,
        _ => Manual,
    };

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="CardSortMode.Manual"/>.</summary>
    public static CardSortMode FromSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        NameAsc => CardSortMode.NameAsc,
        NameDesc => CardSortMode.NameDesc,
        ModifiedAsc => CardSortMode.ModifiedAsc,
        ModifiedDesc => CardSortMode.ModifiedDesc,
        _ => CardSortMode.Manual,
    };

    public static bool IsByModified(CardSortMode mode) => mode is CardSortMode.ModifiedAsc or CardSortMode.ModifiedDesc;
}
