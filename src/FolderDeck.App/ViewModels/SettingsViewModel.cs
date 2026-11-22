using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Stores;

namespace FolderDeck.ViewModels;

/// <summary>Settings dialog (§14). Values apply only on OK.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Colour theme: follow Windows, light or dark. The three switches below show and set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark))]
    private AppTheme _theme;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _confirmExecutables;

    [ObservableProperty]
    private bool _includeWindowsRecent;

    [ObservableProperty]
    private bool _addToWindowsRecent;

    [ObservableProperty]
    private bool _openExplorerInNewTab;

    [ObservableProperty]
    private bool _openOnDoubleClick;

    /// <summary>Right-click menu of items: the app's or Explorer's. The two switches below show and set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItemMenuApp), nameof(IsItemMenuWindows))]
    private ItemMenuStyle _itemMenu;

    [ObservableProperty]
    private string _recentLimitText;

    [ObservableProperty]
    private string _frequentLimitText;

    [ObservableProperty]
    private string _thumbCacheMbText;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _error;

    public SettingsViewModel(AppSettings settings)
    {
        _theme = AppThemes.FromSetting(settings.Theme);
        _showHidden = settings.ShowHidden;
        _confirmExecutables = settings.ConfirmExecutables;
        _includeWindowsRecent = settings.IncludeWindowsRecent;
        _addToWindowsRecent = settings.AddToWindowsRecent;
        _openExplorerInNewTab = settings.OpenExplorerInNewTab;
        _openOnDoubleClick = settings.OpenOnDoubleClick;
        _itemMenu = ItemMenuStyles.FromSetting(settings.ItemMenu);
        _recentLimitText = settings.RecentLimit.ToString(CultureInfo.InvariantCulture);
        _frequentLimitText = settings.FrequentLimit.ToString(CultureInfo.InvariantCulture);
        _thumbCacheMbText = settings.ThumbCacheMb.ToString(CultureInfo.InvariantCulture);
    }

    // One switch per theme (RadioButtons). Only "checked" counts: the one that gets unchecked
    // writes false, which must not change anything.
    public bool IsThemeSystem
    {
        get => Theme == AppTheme.System;
        set => SelectTheme(value, AppTheme.System);
    }

    public bool IsThemeLight
    {
        get => Theme == AppTheme.Light;
        set => SelectTheme(value, AppTheme.Light);
    }

    public bool IsThemeDark
    {
        get => Theme == AppTheme.Dark;
        set => SelectTheme(value, AppTheme.Dark);
    }

    // Same as the theme: only "checked" counts.
    public bool IsItemMenuApp
    {
        get => ItemMenu == ItemMenuStyle.App;
        set => SelectItemMenu(value, ItemMenuStyle.App);
    }

    public bool IsItemMenuWindows
    {
        get => ItemMenu == ItemMenuStyle.Windows;
        set => SelectItemMenu(value, ItemMenuStyle.Windows);
    }

    public Func<string?>? ResetStatsAction { get; init; }

    public Action? OpenDataFolderAction { get; init; }

    public int RecentLimit { get; private set; }

    public int FrequentLimit { get; private set; }

    public int ThumbCacheMb { get; private set; }

    /// <summary>Checks the numeric fields; sets <see cref="Error"/> when they are out of range.</summary>
    public bool Validate()
    {
        if (!int.TryParse(RecentLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int recent) || recent is < 10 or > 500)
        {
            Error = "最近の履歴の表示件数は 10〜500 の整数で入力してください。";
            return false;
        }

        if (!int.TryParse(FrequentLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frequent) || frequent is < 10 or > 200)
        {
            Error = "よく使う項目の表示件数は 10〜200 の整数で入力してください。";
            return false;
        }

        if (!int.TryParse(ThumbCacheMbText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cacheMb) || cacheMb is < 0 or > AppSettings.ThumbCacheMbMax)
        {
            Error = $"画像サムネイルのキャッシュ上限は 0〜{AppSettings.ThumbCacheMbMax} の整数（MB）で入力してください。";
            return false;
        }

        RecentLimit = recent;
        FrequentLimit = frequent;
        ThumbCacheMb = cacheMb;
        Error = null;
        return true;
    }

    private void SelectTheme(bool selected, AppTheme theme)
    {
        if (selected)
        {
            Theme = theme;
        }
    }

    private void SelectItemMenu(bool selected, ItemMenuStyle style)
    {
        if (selected)
        {
            ItemMenu = style;
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.Theme = AppThemes.ToSetting(Theme);
        settings.ShowHidden = ShowHidden;
        settings.ConfirmExecutables = ConfirmExecutables;
        settings.IncludeWindowsRecent = IncludeWindowsRecent;
        settings.AddToWindowsRecent = AddToWindowsRecent;
        settings.OpenExplorerInNewTab = OpenExplorerInNewTab;
        settings.OpenOnDoubleClick = OpenOnDoubleClick;
        settings.ItemMenu = ItemMenuStyles.ToSetting(ItemMenu);
        settings.RecentLimit = RecentLimit;
        settings.FrequentLimit = FrequentLimit;
        settings.ThumbCacheMb = ThumbCacheMb;
    }

    [RelayCommand]
    private void ResetStats()
    {
        Status = ResetStatsAction?.Invoke() ?? Status;
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenDataFolderAction?.Invoke();
}
