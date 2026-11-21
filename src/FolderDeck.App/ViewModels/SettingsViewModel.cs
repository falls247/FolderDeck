using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Stores;

namespace FolderDeck.ViewModels;

/// <summary>Settings dialog (§14). Values apply only on OK.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
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
    private string _recentLimitText;

    [ObservableProperty]
    private string _frequentLimitText;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _error;

    public SettingsViewModel(AppSettings settings)
    {
        _showHidden = settings.ShowHidden;
        _confirmExecutables = settings.ConfirmExecutables;
        _includeWindowsRecent = settings.IncludeWindowsRecent;
        _addToWindowsRecent = settings.AddToWindowsRecent;
        _openExplorerInNewTab = settings.OpenExplorerInNewTab;
        _recentLimitText = settings.RecentLimit.ToString(CultureInfo.InvariantCulture);
        _frequentLimitText = settings.FrequentLimit.ToString(CultureInfo.InvariantCulture);
    }

    public Func<string?>? ResetStatsAction { get; init; }

    public Action? OpenDataFolderAction { get; init; }

    public int RecentLimit { get; private set; }

    public int FrequentLimit { get; private set; }

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

        RecentLimit = recent;
        FrequentLimit = frequent;
        Error = null;
        return true;
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.ShowHidden = ShowHidden;
        settings.ConfirmExecutables = ConfirmExecutables;
        settings.IncludeWindowsRecent = IncludeWindowsRecent;
        settings.AddToWindowsRecent = AddToWindowsRecent;
        settings.OpenExplorerInNewTab = OpenExplorerInNewTab;
        settings.RecentLimit = RecentLimit;
        settings.FrequentLimit = FrequentLimit;
    }

    [RelayCommand]
    private void ResetStats()
    {
        Status = ResetStatsAction?.Invoke() ?? Status;
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenDataFolderAction?.Invoke();
}
