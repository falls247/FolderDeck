namespace FolderDeck.Views;

/// <summary>
/// Hover-intent timings (§9.6). Kept together so they are easy to tune after user feedback (R1).
/// </summary>
public static class HoverTimings
{
    /// <summary>Hover on a card before its overlay appears.</summary>
    public static readonly TimeSpan Overlay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The same while the main window is not the active one: longer, so that a pointer crossing a
    /// FolderDeck that shows behind another app opens nothing.
    /// </summary>
    public static readonly TimeSpan OverlayInactive = TimeSpan.FromMilliseconds(500);

    /// <summary>How often a cascade opened over the inactive main window checks that the foreground window is still the same.</summary>
    public static readonly TimeSpan ForegroundWatch = TimeSpan.FromMilliseconds(250);

    /// <summary>Rest on a folder row before its child flyout opens.</summary>
    public static readonly TimeSpan Open = TimeSpan.FromMilliseconds(150);

    /// <summary>Rest on an image / presentation row before its preview appears.</summary>
    public static readonly TimeSpan Preview = TimeSpan.FromMilliseconds(150);

    /// <summary>Time on a file row / blank space before the open child closes.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    /// <summary>Time outside the card and every flyout before everything closes.</summary>
    public static readonly TimeSpan Exit = TimeSpan.FromMilliseconds(300);

    public static readonly TimeSpan OverlayFadeIn = TimeSpan.FromMilliseconds(150);

    public static readonly TimeSpan OverlayFadeOut = TimeSpan.FromMilliseconds(120);

    public static readonly TimeSpan FlyoutFadeIn = TimeSpan.FromMilliseconds(90);

    /// <summary>A listing slower than this shows "応答がありません（待機中…）" (§7.5).</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(5);
}
