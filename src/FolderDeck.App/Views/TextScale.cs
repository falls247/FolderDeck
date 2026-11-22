using System.Windows;
using FolderDeck.Core.Stores;

namespace FolderDeck.Views;

/// <summary>
/// The 文字の大きさ setting (<see cref="AppSettings.TextScale"/>): scales the text of the content
/// areas (sidebar, cards, details list, cascade lists) and the rows that hold it. The tab strip,
/// toolbar, menus, dialogs, message bar, tooltips and the preview keep their sizes.
/// <para>
/// <see cref="Apply"/> writes the sizes into the application resources under the <c>Size.*</c> keys
/// below; the views read them with <c>{DynamicResource}</c>, so a change shows at once. Themes/Styles.xaml
/// declares the same keys with the 100 % values (the base constants here), so XAML resolves them
/// before the first <see cref="Apply"/>: keep the two in step.
/// </para>
/// <para>
/// Fonts scale linearly. A row keeps its padding and grows with its text only
/// (<see cref="RowHeight"/>), rounded to whole DIP. Layout rounding still snaps that to device
/// pixels (30 DIP is 30.4 DIP at 125 %), so code that steps through rows by their height must use
/// a laid-out row's ActualHeight (as DetailsListView.HitTestInsertion does), not the resource value.
/// </para>
/// </summary>
public static class TextScale
{
    // ---- base sizes at 100 % (DIP) ------------------------------------------------------------

    public const double SidebarFont = 13;
    public const double SidebarHintFont = 12;
    public const double SidebarRowBase = 30;

    public const double CardNameFont = 12.5;
    public const double CardNameLineBase = 16.875;
    public const double CardSubFont = 11;

    public const double DetailsHeaderFont = 12;
    public const double DetailsHeaderBase = 28;
    public const double DetailsNameFont = 12.5;
    public const double DetailsSubFont = 11.5;
    public const double DetailsCellFont = 12;
    public const double DetailsRowBase = 28;

    public const double ListFont = 12;
    public const double ListRowBase = 24;
    public const double OverlayHeaderFont = 11;
    public const double FlyoutTitleFont = 12.5;
    public const double FlyoutTitleLineBase = 17;
    public const double FlyoutCountFont = 11;

    /// <summary>The inline rename box (style RenameBox) over a card's or a details row's name.</summary>
    public const double RenameFont = 12.5;

    /// <summary>Lines of the card's name box and of the flyout's title.</summary>
    private const int CardNameLines = 2;
    private const int FlyoutTitleLines = 3;

    /// <summary>Height of one line of text per DIP of font size (what a row adds per DIP its text grows).</summary>
    private const double LineFactor = 1.35;

    // ---- resource keys (Themes/Styles.xaml) -----------------------------------------------------

    public const string SidebarFontKey = "Size.Sidebar.Font";
    public const string SidebarHintFontKey = "Size.Sidebar.HintFont";
    public const string SidebarRowKey = "Size.Sidebar.Row";
    public const string CardNameFontKey = "Size.Card.NameFont";
    public const string CardNameLineKey = "Size.Card.NameLine";
    public const string CardNameBoxKey = "Size.Card.NameBox";
    public const string CardSubFontKey = "Size.Card.SubFont";
    public const string DetailsHeaderFontKey = "Size.Details.HeaderFont";
    public const string DetailsHeaderKey = "Size.Details.Header";
    public const string DetailsNameFontKey = "Size.Details.NameFont";
    public const string DetailsSubFontKey = "Size.Details.SubFont";
    public const string DetailsCellFontKey = "Size.Details.CellFont";
    public const string DetailsRowKey = "Size.Details.Row";
    public const string ListFontKey = "Size.List.Font";
    public const string ListRowKey = "Size.List.Row";
    public const string OverlayHeaderFontKey = "Size.Overlay.HeaderFont";
    public const string FlyoutTitleFontKey = "Size.Flyout.TitleFont";
    public const string FlyoutTitleLineKey = "Size.Flyout.TitleLine";
    public const string FlyoutTitleBoxKey = "Size.Flyout.TitleBox";
    public const string FlyoutCountFontKey = "Size.Flyout.CountFont";
    public const string RenameFontKey = "Size.Rename.Font";

    /// <summary>The applied setting (percent, already clamped).</summary>
    public static int Percent { get; private set; } = AppSettings.TextScaleDefault;

    /// <summary><see cref="Percent"/> as a factor (1 = the standard size).</summary>
    public static double Factor => Percent / 100.0;

    /// <summary>Height of a row of the details list at the applied scale (resource <see cref="DetailsRowKey"/>).</summary>
    public static double DetailsRowHeight => RowHeight(DetailsRowBase, DetailsNameFont);

    /// <summary>
    /// Height of a row that is <paramref name="baseHeight"/> high at 100 % with text of
    /// <paramref name="font"/>: the padding stays, the text's line grows.
    /// </summary>
    public static double RowHeight(double baseHeight, double font) =>
        Math.Round(baseHeight + ((Factor - 1) * font * LineFactor));

    /// <summary>Applies <paramref name="percent"/> (clamped like the setting) to every view. UI thread.</summary>
    public static void Apply(int percent)
    {
        Percent = AppSettings.ClampTextScale(percent);
        if (Application.Current is not { } app)
        {
            return;
        }

        double f = Factor;
        var r = app.Resources; // top level: wins over the defaults in the merged Styles.xaml
        r[SidebarFontKey] = SidebarFont * f;
        r[SidebarHintFontKey] = SidebarHintFont * f;
        r[SidebarRowKey] = RowHeight(SidebarRowBase, SidebarFont);

        r[CardNameFontKey] = CardNameFont * f;
        r[CardNameLineKey] = CardNameLineBase * f;
        r[CardNameBoxKey] = CardNameLineBase * f * CardNameLines;
        r[CardSubFontKey] = CardSubFont * f;

        r[DetailsHeaderFontKey] = DetailsHeaderFont * f;
        r[DetailsHeaderKey] = RowHeight(DetailsHeaderBase, DetailsHeaderFont);
        r[DetailsNameFontKey] = DetailsNameFont * f;
        r[DetailsSubFontKey] = DetailsSubFont * f;
        r[DetailsCellFontKey] = DetailsCellFont * f;
        r[DetailsRowKey] = DetailsRowHeight;

        r[ListFontKey] = ListFont * f;
        r[ListRowKey] = RowHeight(ListRowBase, ListFont);
        r[OverlayHeaderFontKey] = OverlayHeaderFont * f;
        r[FlyoutTitleFontKey] = FlyoutTitleFont * f;
        r[FlyoutTitleLineKey] = FlyoutTitleLineBase * f;
        r[FlyoutTitleBoxKey] = FlyoutTitleLineBase * f * FlyoutTitleLines;
        r[FlyoutCountFontKey] = FlyoutCountFont * f;

        r[RenameFontKey] = RenameFont * f;
    }
}
