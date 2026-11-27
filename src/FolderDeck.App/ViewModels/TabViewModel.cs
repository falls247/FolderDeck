using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Ranking;
using FolderDeck.Core.Selection;
using FolderDeck.Core.Stores;
using FolderDeck.Core.Text;
using FolderDeck.Services;
using FolderDeck.Shell;
using FolderDeck.Views;

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
/// are fixed tabs with a top level (one instance each for the whole session, on the tab strip only
/// while <see cref="IsOpen"/>); a folder tab (<see cref="TabKind.Folder"/>) is opened from the
/// 登録フォルダ sidebar and always shows a folder. The placeholder (<see cref="TabKind.None"/>) is
/// what is selected while no tab is open: it has no title, no crumb, no cards and no commands.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, ICascadeAnchor
{
    private static readonly TimeSpan RecentCheckTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(2);

    private readonly AppServices _services;
    private readonly DispatcherTimer _slowTimer = new() { Interval = SlowAfter };
    private CancellationTokenSource? _loadCts;

    /// <summary>The order before sorting: the manual order at the top level, the default order inside folders.</summary>
    private IReadOnlyList<CardViewModel> _baseCards = [];

    /// <summary>The listing the folder's cards were made from (null: no folder listed), compared by the auto refresh.</summary>
    private FolderListing? _shownListing;
    private bool _baseIsTop = true;

    /// <summary>The base order with the sort mode applied, before the filter (<see cref="Cards"/> is what the filter leaves).</summary>
    private IReadOnlyList<CardViewModel> _orderedCards = [];

    /// <summary>The paths of the pending cut of this app (dimmed), or null; the same set on every tab.</summary>
    private IReadOnlySet<string>? _cutPaths;

    /// <summary>
    /// The location whose last load ended well (Ok, no banner); null after a change of location and
    /// while nothing was loaded there yet. Paste goes only into a folder that is really on screen.
    /// </summary>
    private NavLocation? _loadedLocation;

    /// <summary>
    /// The <see cref="FolderIdentity"/> of the folder at <see cref="_identityLocation"/>: taken after
    /// each load that listed it, kept when the location follows a rename (<see cref="MoveTo"/>).
    /// </summary>
    private string? _identity;
    private NavLocation? _identityLocation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTop), nameof(ShowClearHistory), nameof(ShowFilter), nameof(CanGoUp), nameof(Title), nameof(ToolTipText))]
    private NavLocation _location;

    /// <summary>The shown cards: sorted, and at the top level filtered (<see cref="Filter"/>).</summary>
    [ObservableProperty]
    private IReadOnlyList<CardViewModel> _cards = [];

    /// <summary>The tab is the one shown (tab strip highlight).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The tab is on the tab strip. Set by <c>MainViewModel</c> together with its <c>Tabs</c>.</summary>
    [ObservableProperty]
    private bool _isOpen;

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

    /// <summary>「3 個を選択」 while items are selected, else null (toolbar, beside the count).</summary>
    [ObservableProperty]
    private string? _selectionText;

    /// <summary>
    /// This tab's sort mode (top level and every folder opened in it). Kept per tab and saved in its
    /// open_tabs entry; a tab that is opened (not restored) starts with the one of the tab used last.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortModeText))]
    private CardSortMode _sortMode;

    /// <summary>What the top level of 最近の履歴 / よく使う項目 shows (all, folders only, files only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText))]
    private ItemFilter _filter;

    /// <summary>
    /// A fixed tab (最近の履歴 / よく使う項目), a folder tab at <paramref name="location"/>, or the
    /// placeholder (<see cref="TabKind.None"/>: no saved sort mode or filter).
    /// </summary>
    public TabViewModel(AppServices services, TabKind kind, NavLocation? location = null)
    {
        _services = services;
        Kind = kind;
        _location = location ?? NavLocation.Top(kind);
        if (kind != TabKind.None)
        {
            var settings = services.Stores.Settings.Current;
            _sortMode = settings.SortModeOf(TabKinds.ToSetting(kind));
            _filter = kind == TabKind.Folder ? ItemFilter.All : settings.FilterOf(TabKinds.ToSetting(kind));
        }

        _slowTimer.Tick += (_, _) =>
        {
            _slowTimer.Stop();
            if (IsLoading)
            {
                IsSlow = true;
            }
        };
        Selection.Changed += () =>
        {
            ApplyItemFlags();
            SelectionText = Selection.Count > 0 ? $"{Selection.Count:N0} 個を選択" : null;
        };
        UpdateBreadcrumbs();
    }

    /// <summary>
    /// The selected items of the shown list, by path (cards are recreated on every load). Only
    /// enabled cards of <see cref="Cards"/> can be in it: <see cref="Show"/> prunes the rest. Cleared
    /// when the tab moves to another location.
    /// </summary>
    public SelectionModel Selection { get; } = new();

    /// <summary>
    /// The paths of this app's pending cut (dimmed cards), or null. <c>MainViewModel</c> sets the same
    /// set on every tab; the cards follow at once.
    /// </summary>
    public IReadOnlySet<string>? CutPaths
    {
        get => _cutPaths;
        set
        {
            _cutPaths = value;
            ApplyItemFlags();
        }
    }

    /// <summary>The paths of the cards that can be selected, in display order (enabled cards of <see cref="Cards"/>).</summary>
    public IReadOnlyList<string> SelectableKeys() =>
        Cards.Where(c => !c.IsDisabled).Select(c => c.Path).ToList();

    /// <summary>The selected cards in display order.</summary>
    public IReadOnlyList<CardViewModel> SelectedCards() =>
        Selection.Count == 0 ? [] : Cards.Where(c => Selection.Contains(c.Path)).ToList();

    /// <summary>
    /// The folder this tab shows, when it is a real folder (inside a folder tab, or a folder opened
    /// inside 最近の履歴 / よく使う項目); null on the top lists and the placeholder.
    /// </summary>
    public string? FolderTargetPath => IsPlaceholder || Location.IsTop ? null : CurrentFolderPath;

    /// <summary>
    /// <see cref="FolderTargetPath"/> only while that folder is what is on screen: its last load
    /// ended well and no load is running. Where a paste without a folder item goes (Ctrl+V, the
    /// menu of the blank area). IsStale is deliberately not tested: RecordUse / TogglePin mark the
    /// 最近の履歴 / よく使う項目 tabs stale while one of them shows a folder inside them, and every
    /// path that really changes the shown folder reloads it at once (IsLoading covers that time).
    /// </summary>
    public string? TransferTargetPath =>
        _loadedLocation is { } loaded && loaded == Location && !IsLoading ? FolderTargetPath : null;

    /// <summary>Counts the loads (<see cref="LoadAsync"/>): an auto refresh that started before one drops its result.</summary>
    public int LoadGeneration { get; private set; }

    /// <summary>When the shown folder was last listed (<c>Environment.TickCount64</c>): a load or an auto refresh.</summary>
    public long LastListedAt { get; private set; }

    /// <summary>
    /// The folder the auto refresh checks: the shown folder while it is really on screen
    /// (<see cref="TransferTargetPath"/>: not a top list, not loading, not an error banner) and was
    /// listed. Null otherwise.
    /// </summary>
    public string? AutoRefreshPath => TransferTargetPath is null ? null : _shownListing?.Path;

    /// <summary>Mirrors the selection and the cut onto the shown cards (<c>IsSelected</c> / <c>IsCut</c>).</summary>
    private void ApplyItemFlags()
    {
        var cut = _cutPaths;
        foreach (var card in Cards)
        {
            card.IsSelected = Selection.Contains(card.Path);
            card.IsCut = cut is not null && cut.Contains(card.Path);
        }
    }

    public TabKind Kind { get; }

    public bool IsFolderTab => Kind == TabKind.Folder;

    /// <summary>The stand-in that is selected while no tab is open (<see cref="TabKind.None"/>).</summary>
    public bool IsPlaceholder => Kind == TabKind.None;

    /// <summary>A fixed tab: 最近の履歴 or よく使う項目 (a top level, a filter, a manual order).</summary>
    private bool IsFixedTab => Kind is TabKind.Recent or TabKind.Frequent;

    /// <summary>Tab strip text: the fixed tab's name, or the name of the folder a folder tab shows.</summary>
    public string Title => IsFolderTab
        ? (CurrentFolderPath is { } path ? PathTools.DisplayName(path) : "（不明）")
        : TabKinds.Title(Kind);

    /// <summary>The folder a tab shows, whatever its kind (null at a top level: the title says it all).</summary>
    public string? ToolTipText => CurrentFolderPath;

    // ---- cascade anchor: the header of a tab that is not shown (FolderTabStrip → CascadeController) ----

    /// <summary>Set by the cascade while the list under this tab's header is showing (the header stays highlighted, its tooltip off).</summary>
    [ObservableProperty]
    private bool _isHoverHeld;

    string ICascadeAnchor.Path => CurrentFolderPath ?? string.Empty;

    NavLocation? ICascadeAnchor.Target => CurrentFolderPath is null ? null : Location;

    /// <summary>
    /// Nothing to list: the shown tab (its content is on screen), the top list of 最近の履歴 / よく使う項目,
    /// an unknown root, or a tab whose last load failed (its banner says why; it is read again when shown).
    /// </summary>
    bool ICascadeAnchor.IsDisabled => IsSelected || CurrentFolderPath is null || BannerText is not null;

    /// <summary>
    /// Outside the shown tab, as a sidebar row: picked folders open in a folder tab (an open tab of that
    /// folder is selected), and the list outlives the shown tab's content (<see cref="ICascadeAnchor.OutlivesContent"/>).
    /// </summary>
    bool ICascadeAnchor.OpensInNewTab => true;

    bool ICascadeAnchor.YieldsToModifiers => false;

    /// <summary>The strip is at the top: the list goes under the header (files too: a look into the tab).</summary>
    bool ICascadeAnchor.OpensBelow => true;

    public bool IsTop => Location.IsTop;

    public bool ShowClearHistory => Kind == TabKind.Recent && IsTop;

    /// <summary>The filter is offered where it applies: the top level of 最近の履歴 / よく使う項目.</summary>
    public bool ShowFilter => IsFixedTab && IsTop;

    public string FilterText => FilterLabels.Short(Filter);

    public string SortModeText => SortModeLabels.Short(SortMode);

    /// <summary>
    /// How this tab shows its items (cards of a shape, or the details list); kept per tab and saved in its
    /// open_tabs entry. <see cref="MainViewModel.CardShape"/> mirrors the shown tab's.
    /// </summary>
    public CardShape CardShape { get; set; } = CardShape.Portrait;

    /// <summary>
    /// The view a tab starts with (saved, or inherited from the tab used last). Only for a tab without cards
    /// (new, restored, or a fixed tab reset by <see cref="ResetToTop"/> when closed), so nothing needs re-ordering.
    /// </summary>
    public void SetViewState(CardShape shape, CardSortMode sort)
    {
        CardShape = shape;
        SortMode = sort;
    }

    /// <summary>
    /// 「1つ上へ」: inside a folder of a fixed tab (its root goes back to the list), anywhere in a folder
    /// tab but at the root of a drive or share (above its root folder, the parent becomes the root).
    /// </summary>
    public bool CanGoUp => IsFolderTab
        ? Location.Rel.Length > 0 || PathTools.ParentOf(Location.Root).Length > 0
        : IsFixedTab && !IsTop;

    /// <summary>Text of the button on the error banner (a folder tab has no list to go back to).</summary>
    public string BackText => IsFolderTab ? "タブを閉じる" : IsFixedTab ? TabKinds.Title(Kind) + "の一覧に戻る" : string.Empty;

    /// <summary>Cards can be dragged to a new position: top level only, not while loading.</summary>
    public bool CanReorder => IsFixedTab && IsTop && _baseIsTop && !IsLoading;

    /// <summary>The tab's data changed while hidden: reload when shown.</summary>
    public bool IsStale { get; set; } = true;

    /// <summary>Back / forward history of this tab (§8.3), newest last.</summary>
    public List<NavEntry> BackStack { get; } = [];

    public List<NavEntry> ForwardStack { get; } = [];

    /// <summary>Scroll position when the tab was left for another one (a folder tab comes back to it).</summary>
    public double SavedScrollOffset { get; set; }

    /// <summary>
    /// Forgets every remembered scroll position (the saved one and those of the back / forward
    /// history): they are offsets of the view that showed the items, and mean nothing in another
    /// view (cards ↔ details list).
    /// </summary>
    public void ResetScrollOffsets()
    {
        SavedScrollOffset = 0;
        foreach (var stack in new[] { BackStack, ForwardStack })
        {
            for (int i = 0; i < stack.Count; i++)
            {
                stack[i] = stack[i] with { ScrollOffset = 0 };
            }
        }
    }

    /// <summary>Absolute path of the current location, or null at the top level.</summary>
    public string? CurrentFolderPath => ResolvePath(Location);

    /// <summary>The identity of the folder at <paramref name="location"/> when it is known (see <see cref="FolderIdentity"/>), else null.</summary>
    public string? IdentityOf(NavLocation location) => _identityLocation == location ? _identity : null;

    /// <summary>The identity of the shown folder, when it is known.</summary>
    public string? Identity => IdentityOf(Location);

    /// <summary>Gives the folder at <paramref name="location"/> its identity (a restored tab, a 登録フォルダ opened in a new tab).</summary>
    public void SetIdentity(NavLocation location, string? identity)
    {
        _identityLocation = identity is null ? null : location;
        _identity = identity;
    }

    /// <summary>
    /// The shown folder (or one of its ancestors) was renamed or moved: the tab goes to where it is
    /// now, still the same folder, so it keeps its identity. The caller reloads it or marks it stale.
    /// </summary>
    public void MoveTo(NavLocation location)
    {
        string? identity = Identity;
        Location = location;
        if (identity is not null)
        {
            SetIdentity(location, identity);
        }
    }

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

    /// <summary>
    /// A fixed tab was closed: it goes back to its top level and forgets its history, cards and
    /// scroll position, so opening it again starts fresh (the instance lives on for the session).
    /// The caller cancels a running load first.
    /// </summary>
    public void ResetToTop()
    {
        Location = NavLocation.Top(Kind);
        BackStack.Clear();
        ForwardStack.Clear();
        ClearCards();
        _baseIsTop = true;
        EmptyText = null;
        BannerText = null;
        NoticeText = null;
        CountText = string.Empty;
        SavedScrollOffset = 0;
        IsStale = true;
    }

    public async Task<LoadOutcome> LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        LoadGeneration++;
        _shownListing = null;

        IsLoading = true;
        IsSlow = false;
        BannerText = null;
        _slowTimer.Stop();
        _slowTimer.Start();
        UpdateBreadcrumbs();
        IsStale = false;
        try
        {
            var location = Location;
            LoadOutcome outcome = location.IsTop ? await LoadTopAsync(ct) : await LoadFolderAsync(ct);
            if (ReferenceEquals(_loadCts, cts))
            {
                // A paste target only once its folder is really listed (not an error banner, not an older load).
                _loadedLocation = outcome == LoadOutcome.Ok && BannerText is null && location == Location ? location : null;
            }

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
            cards.Add(NewEntryCard(e, pins));
        }

        ShowCards(cards, isTop: false);
        ApplyFolderSummary(listing, cards.Count);
        _shownListing = listing;
        LastListedAt = Environment.TickCount64;
        _ = CaptureIdentityAsync(Location, path);
        return LoadOutcome.Ok;
    }

    /// <summary>
    /// Takes the identity of the folder just listed (off the UI thread, 2 s at most), for following it
    /// if it is renamed or moved later. Kept only while the tab is still there; a failure keeps none.
    /// </summary>
    private async Task CaptureIdentityAsync(NavLocation location, string path)
    {
        try
        {
            string? identity = await Task.Run(() => FolderIdentity.Capture(path)).WaitAsync(IdentityTimeout);
            if (Location == location)
            {
                SetIdentity(location, identity);
            }
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Identity of {path} could not be read", ex);
        }
    }

    /// <summary>The card of one entry of the shown folder.</summary>
    private CardViewModel NewEntryCard(in FsEntry e, PathSet pins)
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

        return card;
    }

    private void ApplyFolderSummary(FolderListing listing, int cardCount)
    {
        CountText = $"フォルダ {listing.FolderCount:N0} / ファイル {listing.FileCount:N0}";
        EmptyText = cardCount == 0 ? "このフォルダは空です。" : null;
    }

    /// <summary>
    /// The auto refresh listed the shown folder again (UI thread). Unchanged entries keep their
    /// cards (thumbnails, counts and covers stay), new and changed ones get new cards, and the
    /// list goes through the tab's sort mode and filter as a load does; the selection keeps what
    /// is still there. Nothing else changes: no loading state, no banner, no breadcrumb. A
    /// subfolder whose own write time changed is listed again for its counts and cover. Returns
    /// true when <see cref="Cards"/> was replaced (the caller restores the scroll position).
    /// </summary>
    public bool ApplyPolledListing(FolderListing fresh, long listedAt)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (IsLoading || _shownListing is not { } shown || !fresh.IsOk || !PathTools.Comparer.Equals(fresh.Path, shown.Path))
        {
            return false;
        }

        var delta = ListingChanges.Compare(shown.Entries, fresh.Entries);
        _shownListing = fresh;
        LastListedAt = listedAt;
        if (delta.IsEmpty)
        {
            return false;
        }

        foreach (string folder in delta.ChangedFolders)
        {
            _services.Listings.Invalidate(folder);
            _services.Covers.Remove(folder);
        }

        var old = new Dictionary<string, CardViewModel>(_baseCards.Count, StringComparer.Ordinal);
        foreach (var card in _baseCards)
        {
            old.TryAdd(card.Path, card);
        }

        var pins = PinSet();
        var cards = new List<CardViewModel>(fresh.Entries.Count);
        foreach (var e in fresh.Entries)
        {
            if (delta.Reusable.Contains(e.FullPath) && old.TryGetValue(e.FullPath, out var kept))
            {
                kept.IsPinned = pins.Contains(kept.Path);
                cards.Add(kept);
            }
            else
            {
                cards.Add(NewEntryCard(e, pins));
            }
        }

        ShowCards(cards, isTop: false);
        ApplyFolderSummary(fresh, cards.Count);
        return true;
    }

    // ---- top level --------------------------------------------------------------------------

    private Task<LoadOutcome> LoadTopAsync(CancellationToken ct) => Kind switch
    {
        TabKind.Recent => LoadRecentAsync(ct),
        TabKind.Frequent => LoadFrequentAsync(ct),
        TabKind.None => LoadPlaceholderAsync(),
        _ => LoadNothingAsync(),
    };

    /// <summary>The placeholder has nothing to list and nothing to say: no cards, no texts, no error.</summary>
    private Task<LoadOutcome> LoadPlaceholderAsync()
    {
        NoticeText = null;
        ClearCards();
        return Task.FromResult(LoadOutcome.Ok);
    }

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
        var cards = kept.Select(k => MakeTopCard(k.Item.Path, KindOf(k.Probe.State, k.Item.Kind), k.Item.LastUsed, pins.Contains(k.Item.Path), k.Probe,
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
            cards.Add(MakeTopCard(entry.Path, KindOf(probe.State, entry.Kind), entry.LastUsed, entry.IsPinned, probe, sub: PathTools.ParentOf(entry.Path)));
        }

        ShowCards(ManualOrder.Apply(cards, _services.Stores.CardOrders.Get(CardOrderList.Frequent), c => c.Path), isTop: true);
        return LoadOutcome.Ok;
    }

    /// <summary>
    /// A card of a top level. The probe that found the item also gives what the sort modes and the
    /// details list show (modified time; the size of a file), so they need no second look at the disk.
    /// </summary>
    private CardViewModel MakeTopCard(string path, CardKind kind, DateTimeOffset? lastUsed, bool pinned, ExistProbe probe, string sub)
    {
        string normalized = PathTools.NormalizeOrSelf(path);
        return new CardViewModel(_services, normalized, kind, kind == CardKind.Folder ? new NavLocation(Kind, normalized, string.Empty) : null, ItemContext.TopCard)
        {
            OwnerTab = Kind,
            SubText = sub,
            LastUsed = lastUsed,
            IsPinned = pinned,
            ModifiedUtc = probe.LastWriteTimeUtc ?? default,
            Length = kind == CardKind.File ? probe.Length : null,
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
        _shownListing = null;
        _baseCards = [];
        _orderedCards = [];
        Cards = [];
        Selection.Clear();
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
    private bool FilterApplies => IsFixedTab && _baseIsTop && Filter != ItemFilter.All;

    /// <summary>Shows <paramref name="ordered"/> (every card, sorted) through the filter.</summary>
    private void Show(IReadOnlyList<CardViewModel> ordered)
    {
        _orderedCards = ordered;
        Cards = FilterApplies ? ItemFilters.Apply(ordered, Filter, c => c.IsFolderLike) : ordered;

        // The selection keeps the items still shown (a reload, a re-sort, the filter); new card
        // instances get their flags here, since a prune that removes nothing raises nothing.
        Selection.Prune(SelectableKeys());
        ApplyItemFlags();
        UpdateTopSummary();
    }

    /// <summary>
    /// Count and empty text of a top level (they depend on the filter); a folder sets its own and
    /// the placeholder has neither.
    /// </summary>
    private void UpdateTopSummary()
    {
        if (!IsFixedTab || !_baseIsTop)
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
        // A folder has no size, and 種類 goes by the extension (a folder has none): see CardOrdering.
        var comparer = CardOrdering.ComparerFor<CardViewModel>(
            SortMode,
            c => c.Name,
            c => CardOrdering.KnownTime(c.ModifiedUtc),
            c => c.IsFolderLike ? null : c.Length,
            c => c.Extension);
        if (comparer is null || baseOrder.Count < 2)
        {
            return baseOrder;
        }

        if (!_baseIsTop)
        {
            // folders stay before files, except by modified time (they interleave by their own time)
            comparer = CardOrdering.InsideFolder(SortMode, c => c.IsFolderLike, comparer);
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
        // The whole path of the shown folder, its drive or share first, so the crumbs read as the
        // path (E:\ 仕事 \ 案件); a fixed tab's list comes before it. A crumb above the tab's root makes
        // that folder the root (as ↑ does in a folder tab; a fixed tab stays where it is instead of
        // going back to its list, as its ↑ does); the placeholder has no crumb. Nothing is left out here:
        // when the bar is too narrow, TrailPanel keeps the end of the path in view.
        var items = new List<(string Text, NavLocation Target, string? Tip)>();
        if (IsFixedTab)
        {
            items.Add((TabKinds.Title(Kind), NavLocation.Top(Kind), null));
        }

        string? fullPath = null;
        if (!Location.IsTop)
        {
            string? rootPath = ResolveRootPath(Location.Root);
            if (rootPath is null)
            {
                items.Add(("（不明）", Location with { Rel = string.Empty }, null));
            }
            else
            {
                // A drive keeps its separator ("E:\"), a share is its server and name ("\\nas\share"), a folder is its name.
                foreach (string folder in PathTools.AncestorsAndSelf(PathTools.ParentOf(rootPath)))
                {
                    items.Add((PathTools.DisplayName(folder), new NavLocation(Location.Tab, folder, string.Empty), folder));
                }

                items.Add((PathTools.DisplayName(rootPath), Location with { Rel = string.Empty }, rootPath));
                fullPath = rootPath;
            }

            string rel = string.Empty;
            foreach (string part in PathTools.SplitRel(Location.Rel))
            {
                rel = PathTools.JoinRel(rel, part);
                string? path = rootPath is null ? null : PathTools.Combine(rootPath, rel);
                items.Add((part, Location with { Rel = rel }, path));
                fullPath = path ?? fullPath;
            }
        }

        var next = items.Select((c, i) => new BreadcrumbItem(c.Text, c.Target, c.Tip, SeparatorAfter(i), i == items.Count - 1)).ToList();
        if (next.Count != Breadcrumbs.Count || !next.Zip(Breadcrumbs).All(p => p.First.SameAs(p.Second)))
        {
            // The same path keeps its crumbs (every load starts here): a crumb is a cascade anchor,
            // and a replaced one would take the list under it away (MainWindow.DropCrumbs).
            Breadcrumbs = next;
        }

        AddressText = fullPath;

        string SeparatorAfter(int i) =>
            i == items.Count - 1 ? string.Empty
            : i == 0 && IsFixedTab ? "›"
            : items[i].Text.EndsWith('\\') ? string.Empty
            : "\\";
    }

    /// <summary>The shown folder's full path (null at a top list or for the placeholder): the tooltip of the address bar's "…".</summary>
    [ObservableProperty]
    private string? _addressText;

    partial void OnLocationChanged(NavLocation value)
    {
        _loadedLocation = null; // nothing of the new location is listed yet
        Selection.Clear();
        UpdateBreadcrumbs();
    }
}
