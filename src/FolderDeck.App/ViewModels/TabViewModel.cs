using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Ordering;
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

/// <summary>
/// One tab (§5.2, §10): its location, its cards, and how to load them. 最近の履歴 and よく使う項目
/// are fixed tabs with a top level; a folder tab (<see cref="TabKind.Folder"/>) is opened from the
/// 登録フォルダ sidebar, always shows a folder and can be closed and moved.
/// </summary>
public sealed partial class TabViewModel : ObservableObject
{
    private static readonly TimeSpan RecentCheckTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(5);

    private readonly AppServices _services;
    private readonly DispatcherTimer _slowTimer = new() { Interval = SlowAfter };
    private CancellationTokenSource? _loadCts;

    /// <summary>The order before sorting: the manual order at the top level, the default order inside folders.</summary>
    private IReadOnlyList<CardViewModel> _baseCards = [];
    private bool _baseIsTop = true;

    /// <summary>The base order with the sort mode applied, before the filter (<see cref="Cards"/> is what the filter leaves).</summary>
    private IReadOnlyList<CardViewModel> _orderedCards = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTop), nameof(ShowClearHistory), nameof(ShowFilter), nameof(CanGoUp), nameof(Title), nameof(ToolTipText))]
    private NavLocation _location;

    /// <summary>The shown cards: sorted, and at the top level filtered (<see cref="Filter"/>).</summary>
    [ObservableProperty]
    private IReadOnlyList<CardViewModel> _cards = [];

    /// <summary>The tab is the one shown (tab strip highlight).</summary>
    [ObservableProperty]
    private bool _isSelected;

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

    /// <summary>This tab's sort mode (top level and every folder opened in it). Folder tabs share one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortModeText))]
    private CardSortMode _sortMode;

    /// <summary>What the top level of 最近の履歴 / よく使う項目 shows (all, folders only, files only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText))]
    private ItemFilter _filter;

    /// <summary>A fixed tab (最近の履歴 / よく使う項目), or a folder tab at <paramref name="location"/>.</summary>
    public TabViewModel(AppServices services, TabKind kind, NavLocation? location = null)
    {
        _services = services;
        Kind = kind;
        _location = location ?? NavLocation.Top(kind);
        var settings = services.Stores.Settings.Current;
        _sortMode = settings.SortModeOf(TabKinds.ToSetting(kind));
        _filter = kind == TabKind.Folder ? ItemFilter.All : settings.FilterOf(TabKinds.ToSetting(kind));
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

    public bool IsFolderTab => Kind == TabKind.Folder;

    /// <summary>Tab strip text: the fixed tab's name, or the name of the folder a folder tab shows.</summary>
    public string Title => IsFolderTab
        ? (CurrentFolderPath is { } path ? PathTools.DisplayName(path) : "（不明）")
        : TabKinds.Title(Kind);

    public string? ToolTipText => IsFolderTab ? CurrentFolderPath : null;

    public bool IsTop => Location.IsTop;

    public bool ShowClearHistory => Kind == TabKind.Recent && IsTop;

    /// <summary>The filter is offered where it applies: the top level of 最近の履歴 / よく使う項目.</summary>
    public bool ShowFilter => !IsFolderTab && IsTop;

    public string FilterText => FilterLabels.Short(Filter);

    public string SortModeText => SortModeLabels.Short(SortMode);

    /// <summary>「1つ上へ」: inside a folder of a fixed tab, or below the root of a folder tab.</summary>
    public bool CanGoUp => IsFolderTab ? Location.Rel.Length > 0 : !IsTop;

    /// <summary>Text of the button on the error banner (a folder tab has no list to go back to).</summary>
    public string BackText => IsFolderTab ? "タブを閉じる" : TabKinds.Title(Kind) + "の一覧に戻る";

    /// <summary>Cards can be dragged to a new position: top level only, not while loading.</summary>
    public bool CanReorder => IsTop && _baseIsTop && !IsLoading;

    /// <summary>The tab's data changed while hidden: reload when shown.</summary>
    public bool IsStale { get; set; } = true;

    /// <summary>Back / forward history of this tab (§8.3), newest last.</summary>
    public List<NavEntry> BackStack { get; } = [];

    public List<NavEntry> ForwardStack { get; } = [];

    /// <summary>Scroll position when the tab was left for another one (a folder tab comes back to it).</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>Absolute path of the current location, or null at the top level.</summary>
    public string? CurrentFolderPath => ResolvePath(Location);

    public string? ResolveRootPath(string root) => root.Length == 0 ? null : root;

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
            ClearCards();
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
            ClearCards();
            EmptyText = null;
            CountText = string.Empty;
            BannerText = "フォルダが見つかりません：" + (ResolveRootPath(Location.Root) ?? Location.Root);
            return LoadOutcome.NotFound;
        }

        var listing = await _services.Listings.GetAsync(path, ct);
        ct.ThrowIfCancellationRequested();
        if (!listing.IsOk)
        {
            ClearCards();
            EmptyText = null;
            CountText = string.Empty;
            BannerText = listing.Status == ListingStatus.NotFound
                ? "フォルダが見つかりません：" + path
                : listing.StatusMessage + "：" + path;
            return listing.Status == ListingStatus.NotFound ? LoadOutcome.NotFound : LoadOutcome.Error;
        }

        var pins = PinSet();
        var cards = new List<CardViewModel>(listing.Entries.Count);
        foreach (var e in listing.Entries)
        {
            var target = Location with { Rel = PathTools.JoinRel(Location.Rel, e.Name) };
            var card = new CardViewModel(_services, e.FullPath, e.IsDirectory ? CardKind.Folder : CardKind.File, e.IsDirectory ? target : null, ItemContext.InnerCard)
            {
                OwnerTab = Kind,
                IsPinned = pins.Contains(e.FullPath),
            };
            card.ModifiedUtc = e.LastWriteTimeUtc;
            if (!e.IsDirectory)
            {
                card.Length = e.Length;
            }

            cards.Add(card);
        }

        ShowCards(cards, isTop: false);
        CountText = $"フォルダ {listing.FolderCount:N0} / ファイル {listing.FileCount:N0}";
        EmptyText = cards.Count == 0 ? "このフォルダは空です。" : null;
        return LoadOutcome.Ok;
    }

    // ---- top level --------------------------------------------------------------------------

    private Task<LoadOutcome> LoadTopAsync(CancellationToken ct) => Kind switch
    {
        TabKind.Recent => LoadRecentAsync(ct),
        TabKind.Frequent => LoadFrequentAsync(ct),
        _ => LoadNothingAsync(),
    };

    /// <summary>A folder tab without a folder (never created that way): an error banner, not a crash.</summary>
    private Task<LoadOutcome> LoadNothingAsync()
    {
        NoticeText = null;
        ClearCards();
        EmptyText = null;
        CountText = string.Empty;
        BannerText = "フォルダが見つかりません。";
        return Task.FromResult(LoadOutcome.NotFound);
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
        var kept = await Existence.FilterProbedAsync(
            candidates,
            c => c.Path,
            settings.RecentLimit,
            (p, t) => Existence.ProbeAsync(_services.FileSystem, p, RecentCheckTimeout, t),
            ct);
        ct.ThrowIfCancellationRequested();

        var now = _services.Clock.UtcNow;
        var pins = PinSet();
        var cards = kept.Select(k => MakeTopCard(k.Item.Path, KindOf(k.Probe.State, k.Item.Kind), k.Item.LastUsed, pins.Contains(k.Item.Path), k.Probe.LastWriteTimeUtc,
            sub: $"{Formatting.RelativeTime(k.Item.LastUsed, now)} · {PathTools.ParentOf(k.Item.Path)}")).ToList();

        ShowCards(ManualOrder.Apply(cards, _services.Stores.CardOrders.Get(CardOrderList.Recent), c => c.Path), isTop: true);
        NoticeText = settings.IncludeWindowsRecent && !tracking
            ? "Windows の設定で最近使った項目の記録が無効になっているため、このアプリで開いた項目だけを表示しています。"
            : null;
        return LoadOutcome.Ok;
    }

    private async Task<LoadOutcome> LoadFrequentAsync(CancellationToken ct)
    {
        NoticeText = null;
        var settings = _services.Stores.Settings.Current;
        var now = _services.Clock.UtcNow;
        var ranking = FrequentRanker.Rank(_services.Stores.History.Items, _services.Stores.Pins.Pins, now);

        Task<ExistProbe> Check(string p, CancellationToken t) => Existence.ProbeAsync(_services.FileSystem, p, RecentCheckTimeout, t);
        var pinned = await Existence.FilterProbedAsync(ranking.Pinned, e => e.Path, int.MaxValue, Check, ct);
        var ranked = await Existence.FilterProbedAsync(ranking.Ranked, e => e.Path, settings.FrequentLimit, Check, ct);
        ct.ThrowIfCancellationRequested();

        var cards = new List<CardViewModel>(pinned.Count + ranked.Count);
        foreach (var (entry, probe) in pinned.Concat(ranked))
        {
            cards.Add(MakeTopCard(entry.Path, KindOf(probe.State, entry.Kind), entry.LastUsed, entry.IsPinned, probe.LastWriteTimeUtc, sub: PathTools.ParentOf(entry.Path)));
        }

        ShowCards(ManualOrder.Apply(cards, _services.Stores.CardOrders.Get(CardOrderList.Frequent), c => c.Path), isTop: true);
        return LoadOutcome.Ok;
    }

    private CardViewModel MakeTopCard(string path, CardKind kind, DateTimeOffset? lastUsed, bool pinned, DateTime? modifiedUtc, string sub)
    {
        string normalized = PathTools.NormalizeOrSelf(path);
        return new CardViewModel(_services, normalized, kind, kind == CardKind.Folder ? new NavLocation(Kind, normalized, string.Empty) : null, ItemContext.TopCard)
        {
            OwnerTab = Kind,
            SubText = sub,
            LastUsed = lastUsed,
            IsPinned = pinned,
            ModifiedUtc = modifiedUtc ?? default,
        };
    }

    private static CardKind KindOf(ExistState state, ItemKind? known) => state switch
    {
        ExistState.Folder => CardKind.Folder,
        ExistState.File => CardKind.File,
        _ => known == ItemKind.Folder ? CardKind.Folder : CardKind.File,
    };

    // ---- order: manual order, sort mode, pinned cards fixed ---------------------------------

    /// <summary>Re-applies the sort mode and the filter to the cards (same card instances, no disk access).</summary>
    public void ApplyOrder()
    {
        if (IsLoading)
        {
            return; // the running load applies the current mode when it finishes
        }

        ShowInPlace(Ordered(_baseCards));
    }

    /// <summary>The order before sorting (the manual order at the top level, the default order inside folders).</summary>
    public IReadOnlyList<CardViewModel> BaseCards => _baseCards;

    /// <summary>
    /// Every card in sorted order, including the ones the filter hides. Pinned cards sit at the same
    /// index here and in <see cref="BaseCards"/>.
    /// </summary>
    public IReadOnlyList<CardViewModel> OrderedCards => _orderedCards;

    /// <summary>The filter hides this card right now (top level of 最近の履歴 / よく使う項目 only).</summary>
    public bool IsShown(CardViewModel card) => !FilterApplies || ItemFilters.Matches(Filter, card.IsFolderLike);

    /// <summary>The given order becomes the base (manual) order: after a drop.</summary>
    public void SetBaseOrder(IReadOnlyList<CardViewModel> order)
    {
        _baseCards = order.ToList();
        ShowInPlace(Ordered(_baseCards));
    }

    /// <summary>
    /// Moves the not-yet-pinned <paramref name="card"/> to <paramref name="slot"/> of the base order
    /// and nothing else: every pinned card keeps its slot, the other cards keep their relative order
    /// in the remaining slots. Called right before the card is pinned in a sort mode with its shown
    /// index as <paramref name="slot"/> (always free: pinned cards sit at the same index in both
    /// orders), so once pinned it stays where it is. Nothing is re-shown here: the pin re-sorts, to
    /// the very order on screen. Returns false (base order unchanged) when the move does not apply.
    /// </summary>
    public bool PlaceInBase(CardViewModel card, int slot)
    {
        var source = _baseCards;
        if (card.IsPinned || slot < 0 || slot >= source.Count || !source.Contains(card)
            || (source[slot].IsPinned && !ReferenceEquals(source[slot], card)))
        {
            return false;
        }

        var others = new Queue<CardViewModel>(source.Where(c => !c.IsPinned && !ReferenceEquals(c, card)));
        var placed = new List<CardViewModel>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            var at = source[i];
            if (at.IsPinned)
            {
                placed.Add(at);
            }
            else if (i == slot)
            {
                placed.Add(card);
            }
            else
            {
                placed.Add(others.Dequeue());
            }
        }

        _baseCards = placed;
        return true;
    }

    /// <summary>Takes one card off the list (unregistered, excluded from the history…).</summary>
    public void RemoveCard(CardViewModel card)
    {
        _baseCards = _baseCards.Where(c => c != card).ToList();

        // In a sort mode the slots shift with the removal: re-sort so pinned cards stay on their base slot
        // (later pins and drops rely on the sorted order being exactly the sorted base order).
        Show(IsLoading || SortMode == CardSortMode.Manual
            ? _orderedCards.Where(c => c != card).ToList()
            : Ordered(_baseCards));
    }

    /// <summary>
    /// Pins changed (from any tab, row or header): refreshes every card's 📌 and, in a sort mode,
    /// re-sorts (a pinned card takes its base-order slot, an unpinned one its sorted place).
    /// Returns true when the shown order changed.
    /// </summary>
    public bool ApplyPins(PathSet pins)
    {
        bool changed = false;
        foreach (var card in _baseCards)
        {
            bool pinned = pins.Contains(card.Path);
            if (card.IsPinned != pinned)
            {
                card.IsPinned = pinned;
                changed = true;
            }
        }

        if (!changed || SortMode == CardSortMode.Manual || IsLoading)
        {
            return false;
        }

        var before = Cards;
        ApplyOrder();
        return !ReferenceEquals(before, Cards);
    }

    /// <summary>Pinned paths right now (pins.json).</summary>
    private PathSet PinSet() => new(_services.Stores.Pins.Pins.Select(p => p.Path));

    private void ClearCards()
    {
        _baseCards = [];
        _orderedCards = [];
        Cards = [];
    }

    /// <summary>
    /// Shows a freshly loaded list. <paramref name="baseOrder"/> is the manual order (top level) or
    /// the default order (inside a folder); the tab's sort mode and filter are applied on top of it.
    /// </summary>
    private void ShowCards(IReadOnlyList<CardViewModel> baseOrder, bool isTop)
    {
        _baseCards = baseOrder;
        _baseIsTop = isTop;
        Show(Ordered(baseOrder));
    }

    /// <summary>The filter applies to what is loaded: the top level of 最近の履歴 / よく使う項目.</summary>
    private bool FilterApplies => !IsFolderTab && _baseIsTop && Filter != ItemFilter.All;

    /// <summary>Shows <paramref name="ordered"/> (every card, sorted) through the filter.</summary>
    private void Show(IReadOnlyList<CardViewModel> ordered)
    {
        _orderedCards = ordered;
        Cards = FilterApplies ? ItemFilters.Apply(ordered, Filter, c => c.IsFolderLike) : ordered;
        UpdateTopSummary();
    }

    /// <summary>Count and empty text of a top level (they depend on the filter); a folder sets its own.</summary>
    private void UpdateTopSummary()
    {
        if (IsFolderTab || !_baseIsTop)
        {
            return;
        }

        int total = _orderedCards.Count;
        int shown = Cards.Count;
        string label = Kind == TabKind.Recent ? "履歴" : "項目";
        CountText = FilterApplies ? $"{label} {shown:N0} / {total:N0}" : $"{label} {total:N0}";
        EmptyText = (total, shown) switch
        {
            (0, _) => Kind == TabKind.Recent
                ? "最近の履歴はありません。"
                : "よく使う項目はまだありません。ファイルやフォルダを開くと、ここに並びます。右クリックの「ピン留め」でも追加できます。",
            (_, 0) => Filter == ItemFilter.Folders
                ? "フォルダはありません（表示：フォルダのみ）。"
                : "ファイルはありません（表示：ファイルのみ）。",
            _ => null,
        };
    }

    partial void OnFilterChanged(ItemFilter value)
    {
        if (!IsLoading)
        {
            Show(_orderedCards); // the running load shows through the new filter when it finishes
        }
    }

    private IReadOnlyList<CardViewModel> Ordered(IReadOnlyList<CardViewModel> baseOrder)
    {
        var comparer = CardOrdering.ComparerFor<CardViewModel>(SortMode, c => c.Name, c => CardOrdering.KnownTime(c.ModifiedUtc));
        if (comparer is null || baseOrder.Count < 2)
        {
            return baseOrder;
        }

        if (!_baseIsTop)
        {
            comparer = CardOrdering.FoldersFirst(c => c.IsFolderLike, comparer); // folders stay before files
        }

        return CardOrdering.SortKeepingPinned(baseOrder, c => c.IsPinned, comparer);
    }

    /// <summary>Shows <paramref name="ordered"/> unless it is already the sorted order (avoids rebuilding the grid).</summary>
    private void ShowInPlace(IReadOnlyList<CardViewModel> ordered)
    {
        var current = _orderedCards;
        if (current.Count == ordered.Count)
        {
            bool same = true;
            for (int i = 0; i < current.Count && same; i++)
            {
                same = ReferenceEquals(current[i], ordered[i]);
            }

            if (same)
            {
                _orderedCards = ordered;
                return;
            }
        }

        Show(ordered);
    }

    // ---- breadcrumbs (§5.3) -----------------------------------------------------------------

    public void UpdateBreadcrumbs()
    {
        // A fixed tab starts at its list; a folder tab at its root folder.
        var items = new List<(string Text, NavLocation Target, string? Tip)>();
        if (!IsFolderTab)
        {
            items.Add((TabKinds.Title(Kind), NavLocation.Top(Kind), null));
        }

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
