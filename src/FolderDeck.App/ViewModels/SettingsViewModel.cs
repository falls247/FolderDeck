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

    /// <summary>文字の大きさ in percent (<see cref="AppSettings.TextScale"/>). <see cref="TextScaleChoices"/> show and set it.</summary>
    [ObservableProperty]
    private int _textScale;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _showDotItems;

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

    /// <summary>常駐モード: ログオン時に起動 (<see cref="AppSettings.StartAtLogin"/>).</summary>
    [ObservableProperty]
    private bool _startAtLogin;

    /// <summary>名前で検索の範囲: the subfolders too (<see cref="AppSettings.SearchScopeRecursive"/>). The two switches below show and set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchFolderOnly), nameof(IsSearchRecursive))]
    private bool _searchRecursive;

    public bool IsSearchFolderOnly
    {
        get => !SearchRecursive;
        set
        {
            if (value)
            {
                SearchRecursive = false;
            }
        }
    }

    public bool IsSearchRecursive
    {
        get => SearchRecursive;
        set
        {
            if (value)
            {
                SearchRecursive = true;
            }
        }
    }

    /// <summary>常駐モード: 閉じても終了しない (<see cref="AppSettings.KeepRunningOnClose"/>).</summary>
    [ObservableProperty]
    private bool _keepRunningOnClose;

    /// <summary>Right-click menu of items: the app's or Explorer's. The two switches below show and set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItemMenuApp), nameof(IsItemMenuWindows))]
    private ItemMenuStyle _itemMenu;

    /// <summary>メッセージの表示: top, bottom or off. The three switches below show and set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessageTop), nameof(IsMessageBottom), nameof(IsMessageOff))]
    private MessagePlacement _messagePlacement;

    /// <summary>フォルダの自動更新: on (every <see cref="AutoRefreshSecondsText"/> seconds) or off. The two switches below show and set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoRefreshOff), nameof(IsAutoRefreshInterval))]
    private bool _autoRefresh;

    [ObservableProperty]
    private string _autoRefreshSecondsText;

    [ObservableProperty]
    private string _recentLimitText;

    [ObservableProperty]
    private string _frequentLimitText;

    [ObservableProperty]
    private string _thumbCacheMbText;

    /// <summary>表示: タブバーの重複を許可する (<see cref="AppSettings.AllowDuplicateTabs"/>).</summary>
    [ObservableProperty]
    private bool _allowDuplicateTabs;

    /// <summary>ペイン: 登録ペインの内容を自動記録する (<see cref="AppSettings.PaneAutoRecord"/>). While on, 「現在のペインを上書きする」 is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverwritePaneNote))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteRegisteredPaneCommand))]
    private bool _paneAutoRecord;

    /// <summary>ペイン: 登録ペインを開くとき同じフォルダのタブを使う (<see cref="AppSettings.PaneReuseOpenTabs"/>).</summary>
    [ObservableProperty]
    private bool _paneReuseOpenTabs;

    /// <summary>ペイン: ペインを閉じたときにタブも閉じる (<see cref="AppSettings.PaneCloseClosesTab"/>).</summary>
    [ObservableProperty]
    private bool _paneCloseClosesTab;

    /// <summary>表示: 名前列に対してカスケードを表示する (<see cref="AppSettings.DetailsCascadeOnNameColumn"/>).</summary>
    [ObservableProperty]
    private bool _detailsCascadeOnNameColumn;

    /// <summary>The name typed for 「現在のペインを登録する」 (not a setting: the button acts at once).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterPanesCommand))]
    private string _newPaneName = string.Empty;

    /// <summary>
    /// Backs <see cref="CurrentRegisteredPaneName"/>: given when the dialog opens, and it becomes the
    /// name registered here (the group on screen then belongs to that new entry).
    /// </summary>
    private string? _currentRegisteredPaneName;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _error;

    public SettingsViewModel(AppSettings settings)
    {
        _theme = AppThemes.FromSetting(settings.Theme);
        _textScale = AppSettings.ClampTextScale(settings.TextScale);
        TextScaleChoices = Enumerable.Range(0, ((AppSettings.TextScaleMax - AppSettings.TextScaleMin) / AppSettings.TextScaleStep) + 1)
            .Select(i => new TextScaleChoice(this, AppSettings.TextScaleMin + (i * AppSettings.TextScaleStep)))
            .ToList();
        _showHidden = settings.ShowHidden;
        _showDotItems = settings.ShowDotItems;
        _confirmExecutables = settings.ConfirmExecutables;
        _includeWindowsRecent = settings.IncludeWindowsRecent;
        _addToWindowsRecent = settings.AddToWindowsRecent;
        _openExplorerInNewTab = settings.OpenExplorerInNewTab;
        _openOnDoubleClick = settings.OpenOnDoubleClick;
        _startAtLogin = settings.StartAtLogin;
        _keepRunningOnClose = settings.KeepRunningOnClose;
        _searchRecursive = settings.SearchesRecursively;
        _itemMenu = ItemMenuStyles.FromSetting(settings.ItemMenu);
        _messagePlacement = MessagePlacements.FromSetting(settings.MessagePosition);
        _autoRefresh = settings.AutoRefresh;
        _autoRefreshSecondsText = settings.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        AutoRefreshSeconds = settings.AutoRefreshSeconds; // kept when the field is left invalid while off
        _recentLimitText = settings.RecentLimit.ToString(CultureInfo.InvariantCulture);
        _frequentLimitText = settings.FrequentLimit.ToString(CultureInfo.InvariantCulture);
        _thumbCacheMbText = settings.ThumbCacheMb.ToString(CultureInfo.InvariantCulture);
        _allowDuplicateTabs = settings.AllowDuplicateTabs;
        _paneAutoRecord = settings.PaneAutoRecord;
        _paneReuseOpenTabs = settings.PaneReuseOpenTabs;
        _paneCloseClosesTab = settings.PaneCloseClosesTab;
        _detailsCascadeOnNameColumn = settings.DetailsCascadeOnNameColumn;
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

    // Same as the theme: only "checked" counts.
    public bool IsMessageTop
    {
        get => MessagePlacement == MessagePlacement.Top;
        set => SelectMessagePlacement(value, MessagePlacement.Top);
    }

    public bool IsMessageBottom
    {
        get => MessagePlacement == MessagePlacement.Bottom;
        set => SelectMessagePlacement(value, MessagePlacement.Bottom);
    }

    public bool IsMessageOff
    {
        get => MessagePlacement == MessagePlacement.Off;
        set => SelectMessagePlacement(value, MessagePlacement.Off);
    }

    // Same as the theme: only "checked" counts.
    public bool IsAutoRefreshOff
    {
        get => !AutoRefresh;
        set
        {
            if (value)
            {
                AutoRefresh = false;
            }
        }
    }

    public bool IsAutoRefreshInterval
    {
        get => AutoRefresh;
        set
        {
            if (value)
            {
                AutoRefresh = true;
            }
        }
    }

    /// <summary>The 文字の大きさ switches, smallest first (100 % is 標準).</summary>
    public IReadOnlyList<TextScaleChoice> TextScaleChoices { get; }

    public Func<string?>? ResetStatsAction { get; init; }

    public Action? OpenDataFolderAction { get; init; }

    /// <summary>「現在のペインを登録する」: registers the pane group on screen under the name; the text for <see cref="Status"/> (null: not registered).</summary>
    public Func<string, string?>? RegisterPanesAction { get; init; }

    /// <summary>「現在のペインを上書きする」: writes the group on screen over its 登録ペイン; the text for <see cref="Status"/> (null: nothing written).</summary>
    public Func<string?>? OverwriteRegisteredPaneAction { get; init; }

    /// <summary>A pane group is on screen when the dialog opens: it can be registered.</summary>
    public bool CanRegisterPanes { get; init; }

    /// <summary>The 登録ペイン the group on screen came from (what 「現在のペインを上書きする」 overwrites); null for none.</summary>
    public string? CurrentRegisteredPaneName
    {
        get => _currentRegisteredPaneName;
        init => _currentRegisteredPaneName = value;
    }

    public string RegisterPanesNote => CanRegisterPanes
        ? "表示中のペインの並びと、各ペインで開いているフォルダ・表示方法を、サイドバーの「登録ペイン」に追加します。"
        : "ペインを分割しているときに登録できます（いまは分割していません）。サイドバーの「登録ペイン」の見出しの右のボタンでも登録できます。";

    public string OverwritePaneNote =>
        CurrentRegisteredPaneName is { } name
            ? PaneAutoRecord
                ? $"表示中のペインは登録ペイン「{name}」のものです。自動記録がオンのあいだは、変わるたびに自動で上書きします。"
                : $"表示中のペインで登録ペイン「{name}」を上書きします。"
            : "表示中のペインは登録ペインから開いたものではありません（登録ペインから開いたペインか、ここで登録したペインで使えます）。";

    /// <summary>False with <c>--data-dir</c>: 「ログオン時に起動」 is kept in settings.json only.</summary>
    public bool ManagesRunKey { get; init; } = true;

    public string StartAtLoginNote => ManagesRunKey
        ? "OK を押すと、この実行ファイルを Windows のスタートアップ（HKCU の Run）に登録・解除します。管理者の権限は要りません。"
        : "--data-dir で起動しているため、Windows のスタートアップには登録・解除しません（設定ファイルにだけ保存します）。";

    public int RecentLimit { get; private set; }

    public int FrequentLimit { get; private set; }

    public int ThumbCacheMb { get; private set; }

    public int AutoRefreshSeconds { get; private set; }

    /// <summary>The field that <see cref="Error"/> is about (the name of its text property), or null: the window shows its tab and focuses it.</summary>
    public string? ErrorField { get; private set; }

    /// <summary>Checks the numeric fields; sets <see cref="Error"/> and <see cref="ErrorField"/> when they are out of range.</summary>
    public bool Validate()
    {
        // The interval counts only while 一定の間隔で更新 is chosen; off, an invalid field keeps the old value.
        bool secondsOk = int.TryParse(AutoRefreshSecondsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            && seconds is >= AppSettings.AutoRefreshSecondsMin and <= AppSettings.AutoRefreshSecondsMax;
        if (AutoRefresh && !secondsOk)
        {
            Error = $"自動更新の間隔は {AppSettings.AutoRefreshSecondsMin}〜{AppSettings.AutoRefreshSecondsMax} の整数（秒）で入力してください。";
            ErrorField = nameof(AutoRefreshSecondsText);
            return false;
        }

        if (!int.TryParse(RecentLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int recent) || recent is < 10 or > 500)
        {
            Error = "最近の履歴の表示件数は 10〜500 の整数で入力してください。";
            ErrorField = nameof(RecentLimitText);
            return false;
        }

        if (!int.TryParse(FrequentLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frequent) || frequent is < 10 or > 200)
        {
            Error = "よく使う項目の表示件数は 10〜200 の整数で入力してください。";
            ErrorField = nameof(FrequentLimitText);
            return false;
        }

        if (!int.TryParse(ThumbCacheMbText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cacheMb) || cacheMb is < 0 or > AppSettings.ThumbCacheMbMax)
        {
            Error = $"画像サムネイルのキャッシュ上限は 0〜{AppSettings.ThumbCacheMbMax} の整数（MB）で入力してください。";
            ErrorField = nameof(ThumbCacheMbText);
            return false;
        }

        RecentLimit = recent;
        FrequentLimit = frequent;
        ThumbCacheMb = cacheMb;
        if (secondsOk)
        {
            AutoRefreshSeconds = seconds;
        }

        Error = null;
        ErrorField = null;
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

    private void SelectMessagePlacement(bool selected, MessagePlacement placement)
    {
        if (selected)
        {
            MessagePlacement = placement;
        }
    }

    partial void OnTextScaleChanged(int value)
    {
        foreach (var choice in TextScaleChoices)
        {
            choice.Refresh();
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.Theme = AppThemes.ToSetting(Theme);
        settings.TextScale = TextScale;
        settings.ShowHidden = ShowHidden;
        settings.ShowDotItems = ShowDotItems;
        settings.ConfirmExecutables = ConfirmExecutables;
        settings.IncludeWindowsRecent = IncludeWindowsRecent;
        settings.AddToWindowsRecent = AddToWindowsRecent;
        settings.OpenExplorerInNewTab = OpenExplorerInNewTab;
        settings.OpenOnDoubleClick = OpenOnDoubleClick;
        settings.StartAtLogin = StartAtLogin;
        settings.KeepRunningOnClose = KeepRunningOnClose;
        settings.SearchScope = SearchRecursive ? AppSettings.SearchScopeRecursive : AppSettings.SearchScopeFolder;
        settings.ItemMenu = ItemMenuStyles.ToSetting(ItemMenu);
        settings.MessagePosition = MessagePlacements.ToSetting(MessagePlacement);
        settings.RecentLimit = RecentLimit;
        settings.FrequentLimit = FrequentLimit;
        settings.ThumbCacheMb = ThumbCacheMb;
        settings.AutoRefresh = AutoRefresh;
        settings.AutoRefreshSeconds = AutoRefreshSeconds;
        settings.AllowDuplicateTabs = AllowDuplicateTabs;
        settings.PaneAutoRecord = PaneAutoRecord;
        settings.PaneReuseOpenTabs = PaneReuseOpenTabs;
        settings.PaneCloseClosesTab = PaneCloseClosesTab;
        settings.DetailsCascadeOnNameColumn = DetailsCascadeOnNameColumn;
    }

    [RelayCommand]
    private void ResetStats()
    {
        Status = ResetStatsAction?.Invoke() ?? Status;
    }

    private bool CanRunRegisterPanes() => CanRegisterPanes && !string.IsNullOrWhiteSpace(NewPaneName);

    /// <summary>「現在のペインを登録する」 (acts at once, as the reset does): the group on screen now belongs to the new entry.</summary>
    [RelayCommand(CanExecute = nameof(CanRunRegisterPanes))]
    private void RegisterPanes()
    {
        string name = NewPaneName.Trim();
        if (RegisterPanesAction?.Invoke(name) is not { } text)
        {
            return;
        }

        Status = text;
        _currentRegisteredPaneName = name;
        OnPropertyChanged(nameof(CurrentRegisteredPaneName));
        OnPropertyChanged(nameof(OverwritePaneNote));
        OverwriteRegisteredPaneCommand.NotifyCanExecuteChanged();
        NewPaneName = string.Empty; // a second press does not register the same group twice
    }

    private bool CanOverwritePanes() => !PaneAutoRecord && CurrentRegisteredPaneName is not null;

    /// <summary>「現在のペインを上書きする」 (acts at once): only while 自動記録 is off in this dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanOverwritePanes))]
    private void OverwriteRegisteredPane()
    {
        Status = OverwriteRegisteredPaneAction?.Invoke() ?? Status;
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenDataFolderAction?.Invoke();
}

/// <summary>One 文字の大きさ switch of the settings dialog (a RadioButton).</summary>
public sealed class TextScaleChoice : ObservableObject
{
    private readonly SettingsViewModel _owner;

    internal TextScaleChoice(SettingsViewModel owner, int percent)
    {
        _owner = owner;
        Percent = percent;
        Label = percent.ToString(CultureInfo.InvariantCulture) + (percent == AppSettings.TextScaleDefault ? "%（標準）" : "%");
    }

    public int Percent { get; }

    public string Label { get; }

    // Same as the theme: only "checked" counts.
    public bool IsSelected
    {
        get => _owner.TextScale == Percent;
        set
        {
            if (value)
            {
                _owner.TextScale = Percent;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}
