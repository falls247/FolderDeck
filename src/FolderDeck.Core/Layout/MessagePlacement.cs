namespace FolderDeck.Core.Layout;

/// <summary>Where the message line (「〜を開きました」 and the like) is shown (settings dialog, saved in settings.json).</summary>
public enum MessagePlacement
{
    /// <summary>Under the toolbar, above the items (the place it always had).</summary>
    Top,

    /// <summary>Under the items, at the bottom of the main area.</summary>
    Bottom,

    /// <summary>
    /// Success messages (〜を開きました, パスをコピーしました) are not shown. Info, warnings and errors
    /// still are, at the top: info also carries the reasons an action was refused, and a refused or
    /// failed operation must not go unnoticed.
    /// </summary>
    Off,
}

/// <summary>Conversion between <see cref="MessagePlacement"/> and its settings.json value.</summary>
public static class MessagePlacements
{
    public const string Top = "top";
    public const string Bottom = "bottom";
    public const string Off = "off";

    public static string ToSetting(MessagePlacement placement) => placement switch
    {
        MessagePlacement.Bottom => Bottom,
        MessagePlacement.Off => Off,
        _ => Top,
    };

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="MessagePlacement.Top"/>.</summary>
    public static MessagePlacement FromSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Bottom => MessagePlacement.Bottom,
        Off => MessagePlacement.Off,
        _ => MessagePlacement.Top,
    };
}
