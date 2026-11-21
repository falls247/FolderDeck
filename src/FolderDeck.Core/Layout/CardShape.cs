namespace FolderDeck.Core.Layout;

/// <summary>Shape of the cards (one setting for every tab, saved in settings.json).</summary>
public enum CardShape
{
    /// <summary>Tall card: the thumbnail is 3:4 (width : height).</summary>
    Portrait,

    /// <summary>The thumbnail is 1:1.</summary>
    Square,
}

/// <summary>Conversion between <see cref="CardShape"/> and its settings.json value, and the thumbnail size it gives.</summary>
public static class CardShapes
{
    public const string Portrait = "portrait";
    public const string Square = "square";

    public static string ToSetting(CardShape shape) => shape == CardShape.Square ? Square : Portrait;

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="CardShape.Portrait"/>.</summary>
    public static CardShape FromSetting(string? value) =>
        string.Equals(value?.Trim(), Square, StringComparison.OrdinalIgnoreCase) ? CardShape.Square : CardShape.Portrait;

    /// <summary>Height of the thumbnail area for a card <paramref name="width"/> wide.</summary>
    public static double ThumbHeight(double width, CardShape shape) =>
        Math.Round(shape == CardShape.Square ? width : width * 4 / 3, 2);
}
