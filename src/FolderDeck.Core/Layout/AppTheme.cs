namespace FolderDeck.Core.Layout;

/// <summary>Colour theme of the app (settings dialog, saved in settings.json).</summary>
public enum AppTheme
{
    /// <summary>Follows Windows' app mode (light / dark), also while the app is running.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Conversion between <see cref="AppTheme"/> and its settings.json value.</summary>
public static class AppThemes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static string ToSetting(AppTheme theme) => theme switch
    {
        AppTheme.Light => Light,
        AppTheme.Dark => Dark,
        _ => System,
    };

    /// <summary>Reads a settings value (case-insensitive); missing or unknown values are <see cref="AppTheme.System"/>.</summary>
    public static AppTheme FromSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Light => AppTheme.Light,
        Dark => AppTheme.Dark,
        _ => AppTheme.System,
    };

    /// <summary>Whether the dark colours are used: the choice itself, or Windows' mode for <see cref="AppTheme.System"/>.</summary>
    public static bool IsDark(AppTheme theme, bool systemIsDark) => theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => systemIsDark,
    };
}
