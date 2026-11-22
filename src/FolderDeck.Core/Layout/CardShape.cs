namespace FolderDeck.Core.Layout;

/// <summary>
/// How the items of a tab are shown (one setting for every tab, saved in settings.json): as cards
/// of one of two shapes, or as the rows of a details list.
/// </summary>
public enum CardShape
{
    /// <summary>Tall card: the thumbnail is 3:4 (width : height).</summary>
    Portrait,

    /// <summary>The thumbnail is 1:1.</summary>
    Square,

    /// <summary>No cards: one row per item with name, modified time, type and size.</summary>
    Details,
}

/// <summary>Conversion between <see cref="CardShape"/> and its settings.json value, and the thumbnail size it gives.</summary>
public static class CardShapes
{
    public const string Portrait = "portrait";
    public const string Square = "square";
    public const string Details = "details";

    public static string ToSetting(CardShape shape) => shape switch
    {
        CardShape.Square => Square,
        CardShape.Details => Details,
        _ => Portrait,
    };

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="CardShape.Portrait"/>.</summary>
    public static CardShape FromSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Square => CardShape.Square,
        Details => CardShape.Details,
        _ => CardShape.Portrait,
    };

    /// <summary>True for the shapes that show cards (the card grid), false for the details list.</summary>
    public static bool IsCards(CardShape shape) => shape != CardShape.Details;

    /// <summary>
    /// Height of the thumbnail area for a card <paramref name="width"/> wide. The details list has
    /// no thumbnail; it gets the portrait height, so a hidden card grid keeps a valid size.
    /// </summary>
    public static double ThumbHeight(double width, CardShape shape) =>
        Math.Round(shape == CardShape.Square ? width : width * 4 / 3, 2);
}
