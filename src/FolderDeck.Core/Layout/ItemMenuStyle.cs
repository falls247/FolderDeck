namespace FolderDeck.Core.Layout;

/// <summary>Which right-click menu items get (settings dialog, saved in settings.json).</summary>
public enum ItemMenuStyle
{
    /// <summary>The app's own menu, with Explorer's menu behind its last entry.</summary>
    App,

    /// <summary>Explorer's own menu, with the app's entries on top of it.</summary>
    Windows,
}

/// <summary>Conversion between <see cref="ItemMenuStyle"/> and its settings.json value.</summary>
public static class ItemMenuStyles
{
    public const string App = "app";
    public const string Windows = "windows";

    public static string ToSetting(ItemMenuStyle style) => style switch
    {
        ItemMenuStyle.Windows => Windows,
        _ => App,
    };

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="ItemMenuStyle.App"/>.</summary>
    public static ItemMenuStyle FromSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Windows => ItemMenuStyle.Windows,
        _ => ItemMenuStyle.App,
    };
}
