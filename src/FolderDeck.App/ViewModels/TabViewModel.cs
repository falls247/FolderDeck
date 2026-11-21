using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Ranking;
using FolderDeck.Core.Stores;
using FolderDeck.Core.Text;
using FolderDeck.Services;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

public enum LoadOutcome
{
    Ok,
    Cancelled,
    NotFound,
    Error,
}

/// <summary>One tab (§5.2, §10): its location, its cards, and how to load them.</summary>
public sealed partial class TabViewModel : ObservableObject
{
    private static readonly TimeSpan RegisteredCheckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecentCheckTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(5);

    private readonly AppServices _services;
    private readonly DispatcherTimer _slowTimer = new() { Interval = SlowAfter };
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTop), nameof(ShowRegisterBar), nameof(ShowClearHistory))]
    private NavLocation _location;

    [ObservableProperty]
    private IReadOnlyList<CardViewModel> _cards = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSlow;

    [ObservableProperty]
    private string? _emptyText;

    [ObservableProperty]
    private string? _bannerText;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BreadcrumbItem> _breadcrumbs = [];

    [ObservableProperty]
    private string? _noticeText;

    public TabViewModel(AppServices services, TabKind kind)
    {
        _services = services;
        Kind = kind;
        Title = TabKinds.Title(kind);
        _location = NavLocation.Top(kind);
        _slowTimer.Tick += (_, _) =>
        {
            _slowTimer.Stop();
            if (IsLoading)
            {
                IsSlow = true;
            }
        };
        UpdateBreadcrumbs();
    }

    public TabKind Kind { get; }

    public string Title { get; }

    public bool IsTop => Location.IsTop;

    public bool ShowRegisterBar => Kind == TabKind.Registered && IsTop;

    public bool ShowClearHistory => Kind == TabKind.Recent && IsTop;

    /// <summary>The tab's data changed while hidden: reload when shown.</summary>
    public bool IsStale { get; set; } = true;

    /// <summary>Absolute path of the current location, or null at the top level / unknown root.</summary>
    public string? CurrentFolderPath => ResolvePath(Location);

    public string? ResolveRootPath(string root)
    {
        if (root.Length == 0)
        {
            return null;
        }

        if (Kind == TabKind.Registered)
        {
            return _services.Stores.Registered.FindById(root)?.Path;
        }

        return root;
    }

    public string? ResolvePath(NavLocation location)
    {
        string? root = ResolveRootPath(location.Root);
        if (root is null)
        {
            return null;
        }

        return PathTools.IsSafeRel(location.Rel) ? PathTools.Combine(root, location.Rel) : null;
    }

    public void CancelLoad()
    {
        _loadCts?.Cancel();
    }

    public async Task<LoadOutcome> LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        IsLoading = true;
        IsSlow = false;
        BannerText = null;
        _slowTimer.Stop();
        _slowTimer.Start();
        UpdateBreadcrumbs();
        IsStale = false;
        try
        {
            LoadOutcome outcome = Location.IsTop ? await LoadTopAsync(ct) : await LoadFolderAsync(ct);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return LoadOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Loading {Kind} {Location} failed", ex);
            Cards = [];
            BannerText = "読み込めません：" + ex.Message;
            return LoadOutcome.Error;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                IsSlow = false;
                _slowTimer.Stop();
            }
        }
    }

    // ---- folder -----------------------------------------------------------------------------

    private async Task<LoadOutcome> LoadFolderAsync(CancellationToken ct)
    {
        NoticeText = null;
        string? path = ResolvePath(Location);
        if (path is null)
        {
            Cards = [];
            EmptyText = null;
            CountText = string.Empty;
            BannerText = "フォルダが見つかりません：" + (ResolveRootPath(Location.Root) ?? Location.Root);
            return LoadOutcome.NotFound;
        }

        var listing = await _services.Listings.GetAsync(path, ct);
        ct.ThrowIfCancellationRequested();
        if (!listing.IsOk)
        {
            Cards = [];
            EmptyText = null;
            CountText = string.Empty;
            BannerText = listing.Status == ListingStatus.NotFound
                ? "フォルダが見つかりません：" + path
                : listing.StatusMessage + "：" + path;
            return listing.Status == ListingStatus.NotFound ? LoadOutcome.NotFound : LoadOutcome.Error;
        }

        var cards = new List<CardViewModel>(listing.Entries.Count);
        foreach (var e in listing.Entries)
        {
            var target = Location with { Rel = PathTools.JoinRel(Location.Rel, e.Name) };
            var card = new CardViewModel(_services, e.FullPath, e.IsDirectory ? CardKind.Folder : CardKind.File, e.IsDirectory ? target : null, ItemContext.InnerCard)
            {
                OwnerTab = Kind,
            };
            card.ModifiedUtc = e.LastWriteTimeUtc;
            if (!e.IsDirectory)
            {
                card.Length = e.Length;
            }

            cards.Add(card);
        }

        Cards = cards;
        CountText = $"フォルダ {listing.FolderCount:N0} / ファイル {listing.FileCount:N0}";
        EmptyText = cards.Count == 0 ? "このフォルダは空です。" : null;
        return LoadOutcome.Ok;
    }

    // ---- top level --------------------------------------------------------------------------

    private Task<LoadOutcome> LoadTopAsync(CancellationToken ct) => Kind switch
    {
        TabKind.Registered => LoadRegisteredAsync(ct),
        TabKind.Recent => LoadRecentAsync(ct),
        _ => LoadFrequentAsync(ct),
    };

    private Task<LoadOutcome> LoadRegisteredAsync(CancellationToken ct)
    {
        NoticeText = null;
        var folders = _services.Stores.Registered.Folders;
        var pins = _services.Stores.Pins;
        var cards = new List<CardViewModel>(folders.Count);
        foreach (var f in folders)
        {
            var card = new CardViewModel(_services, f.Path, CardKind.Checking, new NavLocation(TabKind.Registered, f.Id, string.Empty), ItemContext.TopCard, f.Id)
            {
                OwnerTab = Kind,
                SubText = f.Path,
                IsPinned = pins.IsPinned(f.Path),
            };
            card.ExistenceTask = CheckRegisteredAsync(card, ct);
            cards.Add(card);
        }

        Cards = cards;
        CountText = $"登録 {cards.Count:N0}";
        EmptyText = cards.Count == 0
            ? "登録フォルダはまだありません。上の入力欄にパスを貼り付けるか「参照…」で追加してください。"
            : null;
        return Task.FromResult(LoadOutcome.Ok);
    }

    private async Task CheckRegisteredAsync(CardViewModel card, CancellationToken ct)
    {
        try
        {
            var state = await Existence.CheckAsync(_services.FileSystem, card.Path, RegisteredCheckTimeout, ct);
            card.SetKind(state switch
            {
                ExistState.Folder => CardKind.Folder,
                ExistState.Timeout => CardKind.NoResponse,
                _ => CardKind.Missing,
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<LoadOutcome> LoadRecentAsync(CancellationToken ct)
    {
        var settings = _services.Stores.Settings.Current;
        IReadOnlyList<WindowsRecentItem> windowsItems = [];
        bool tracking = true;
        if (settings.IncludeWindowsRecent)
        {
            tracking = WindowsRecent.IsTrackingEnabled();
            try
            {
                windowsItems = await _services.Shell.RunAsync(() => WindowsRecent.Read(ct), ct, TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException ex)
            {
                AppLog.Warn("Reading Windows recent items timed out", ex);
            }
        }

        var history = _services.Stores.History.Items;
        var excluded = _services.Stores.Excluded.Items;
        var candidates = await Task.Run(() => RecentMerger.Merge(windowsItems, history, excluded, settings.RecentClearedAt), ct);
        var kept = await Existence.FilterAsync(
            candidates,
            c => c.Path,
            settings.RecentLimit,
            (p, t) => Existence.CheckAsync(_services.FileSystem, p, RecentCheckTimeout, t),
            ct);
        ct.ThrowIfCancellationRequested();

        var now = _services.Clock.UtcNow;
        var pins = _services.Stores.Pins;
        var cards = kept.Select(k => MakeTopCard(k.Item.Path, KindOf(k.State, k.Item.Kind), k.Item.LastUsed, pins.IsPinned(k.Item.Path), showPinBadge: false,
            sub: $"{Formatting.RelativeTime(k.Item.LastUsed, now)} · {PathTools.ParentOf(k.Item.Path)}")).ToList();

        Cards = cards;
        CountText = $"履歴 {cards.Count:N0}";
        NoticeText = settings.IncludeWindowsRecent && !tracking
            ? "Windows の設定で最近使った項目の記録が無効になっているため、このアプリで開いた項目だけを表示しています。"
            : null;
        EmptyText = cards.Count == 0 ? "最近の履歴はありません。" : null;
        return LoadOutcome.Ok;
    }

    private async Task<LoadOutcome> LoadFrequentAsync(CancellationToken ct)
    {
        NoticeText = null;
        var settings = _services.Stores.Settings.Current;
        var now = _services.Clock.UtcNow;
        var ranking = FrequentRanker.Rank(_services.Stores.History.Items, _services.Stores.Pins.Pins, now);

        Task<ExistState> Check(string p, CancellationToken t) => Existence.CheckAsync(_services.FileSystem, p, RecentCheckTimeout, t);
        var pinned = await Existence.FilterAsync(ranking.Pinned, e => e.Path, int.MaxValue, Check, ct);
        var ranked = await Existence.FilterAsync(ranking.Ranked, e => e.Path, settings.FrequentLimit, Check, ct);
        ct.ThrowIfCancellationRequested();

        var cards = new List<CardViewModel>(pinned.Count + ranked.Count);
        foreach (var (entry, state) in pinned.Concat(ranked))
        {
            cards.Add(MakeTopCard(entry.Path, KindOf(state, entry.Kind), entry.LastUsed, entry.IsPinned, showPinBadge: entry.IsPinned, sub: PathTools.ParentOf(entry.Path)));
        }

        Cards = cards;
        CountText = $"項目 {cards.Count:N0}";
        EmptyText = cards.Count == 0
            ? "よく使う項目はまだありません。ファイルやフォルダを開くと、ここに並びます。右クリックの「ピン留め」でも追加できます。"
            : null;
        return LoadOutcome.Ok;
    }

    private CardViewModel MakeTopCard(string path, CardKind kind, DateTimeOffset? lastUsed, bool pinned, bool showPinBadge, string sub)
    {
        string normalized = PathTools.NormalizeOrSelf(path);
        return new CardViewModel(_services, normalized, kind, kind == CardKind.Folder ? new NavLocation(Kind, normalized, string.Empty) : null, ItemContext.TopCard)
        {
            OwnerTab = Kind,
            SubText = sub,
            LastUsed = lastUsed,
            IsPinned = pinned,
            ShowPinBadge = showPinBadge,
        };
    }

    private static CardKind KindOf(ExistState state, ItemKind? known) => state switch
    {
        ExistState.Folder => CardKind.Folder,
        ExistState.File => CardKind.File,
        _ => known == ItemKind.Folder ? CardKind.Folder : CardKind.File,
    };

    // ---- breadcrumbs (§5.3) -----------------------------------------------------------------

    public void UpdateBreadcrumbs()
    {
        var items = new List<(string Text, NavLocation Target, string? Tip)>
        {
            (Title, NavLocation.Top(Kind), null),
        };
        if (!Location.IsTop)
        {
            string? rootPath = ResolveRootPath(Location.Root);
            string rootName = rootPath is null ? "（不明）" : PathTools.DisplayName(rootPath);
            items.Add((rootName, Location with { Rel = string.Empty }, rootPath));
            string rel = string.Empty;
            foreach (string part in PathTools.SplitRel(Location.Rel))
            {
                rel = PathTools.JoinRel(rel, part);
                items.Add((part, Location with { Rel = rel }, rootPath is null ? null : PathTools.Combine(rootPath, rel)));
            }
        }

        // Long trails: replace middle segments with "…" (first two and the last one always stay).
        const int MaxChars = 72;
        var crumbs = items.Select(i => (i.Text, (NavLocation?)i.Target, i.Tip)).ToList();
        int Total() => crumbs.Sum(c => c.Text.Length + 3);
        if (Total() > MaxChars && crumbs.Count > 4)
        {
            var hidden = new List<string>();
            int ellipsisAt = 2;
            crumbs.Insert(ellipsisAt, ("…", null, null));
            while (Total() > MaxChars && crumbs.Count > 5)
            {
                hidden.Add(crumbs[ellipsisAt + 1].Text);
                crumbs.RemoveAt(ellipsisAt + 1);
            }

            if (hidden.Count == 0)
            {
                crumbs.RemoveAt(ellipsisAt);
            }
            else
            {
                crumbs[ellipsisAt] = ("…", null, string.Join(" / ", hidden));
            }
        }

        Breadcrumbs = crumbs.Select((c, i) => new BreadcrumbItem(c.Text, c.Item2, c.Tip, i == crumbs.Count - 1)).ToList();
    }

    partial void OnLocationChanged(NavLocation value) => UpdateBreadcrumbs();
}
