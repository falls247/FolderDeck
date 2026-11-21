using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Text;
using FolderDeck.Services;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

public enum CardKind
{
    /// <summary>Registered folder whose existence check is still running (looks like a folder).</summary>
    Checking,
    Folder,
    File,

    /// <summary>Registered folder that no longer exists (§6.3).</summary>
    Missing,

    /// <summary>Registered folder whose existence check took longer than 2 s (§6.3).</summary>
    NoResponse,
}

public enum BadgeMode
{
    Pending,
    Folder,
    File,
    Missing,
    NoResponse,
    Error,
}

/// <summary>
/// One card (§6). Created with only a name and kind; details (counts, child list, cover and
/// thumbnail) load when the card is realized on screen and are cancelled when it scrolls away
/// (§7.4.1).
/// </summary>
public sealed partial class CardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;
    private int _realized;
    private int _thumbPx;
    private int _iconPx;
    private string? _coverPath;
    private bool _coverSearched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderLike), nameof(IsDisabled), nameof(BadgeMode), nameof(ActionText), nameof(ToolTipText), nameof(ShowWarning))]
    private CardKind _kind;

    [ObservableProperty]
    private string? _subText;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private ThumbMode _thumbMode;

    /// <summary>Pinned (📌 badge on every tab, kept in sync with pins.json).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveToolTip))]
    private bool _isPinned;

    /// <summary>The card is being dragged to a new position (drawn semi-transparent).</summary>
    [ObservableProperty]
    private bool _isDragged;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isHoverHeld;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeMode), nameof(ShowFolderCount), nameof(ShowFileCount), nameof(FolderCountText), nameof(FileCountText))]
    private bool _countsLoaded;

    private int _folderCount;
    private int _fileCount;
    private string? _errorBadge;

    public CardViewModel(AppServices services, string path, CardKind kind, NavLocation? target, ItemContext context, string? registeredId = null)
    {
        _services = services;
        Path = path;
        Name = PathTools.DisplayName(path);
        _kind = kind;
        Target = target;
        Context = context;
        RegisteredId = registeredId;
        Extension = kind == CardKind.File ? PathTools.ExtensionOf(path) : string.Empty;
    }

    /// <summary>Raised on the UI thread once counts and the child list are known.</summary>
    public event Action<CardViewModel>? DetailsLoaded;

    public string Path { get; }

    public string Name { get; }

    /// <summary>Where clicking a folder card navigates (null for files).</summary>
    public NavLocation? Target { get; }

    public ItemContext Context { get; }

    public bool IsTopLevel => Context == ItemContext.TopCard;

    public string? RegisteredId { get; }

    public TabKind Tab => Target?.Tab ?? OwnerTab;

    public TabKind OwnerTab { get; init; }

    public string Extension { get; }

    public long? Length { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>Existence check of a registered folder (§6.3), if one is running.</summary>
    public Task? ExistenceTask { get; set; }

    public FolderListing? Listing { get; private set; }

    public bool IsFolderLike => Kind is CardKind.Folder or CardKind.Checking;

    public bool IsFolder => Kind == CardKind.Folder;

    public bool IsFile => Kind == CardKind.File;

    public bool IsDisabled => Kind is CardKind.Missing or CardKind.NoResponse;

    public bool ShowWarning => IsDisabled;

    public bool HasChildren => CountsLoaded && Listing is { IsOk: true } && (_folderCount + _fileCount) > 0;

    public bool CanRemove => IsTopLevel;

    public string RemoveToolTip => OwnerTab switch
    {
        TabKind.Registered => "登録を解除（フォルダ内のファイルは削除されません）",
        TabKind.Recent => "履歴から外す（ファイルは削除されません）",
        _ => IsPinned ? "ピン留めを外す" : "一覧から外す",
    };

    public BadgeMode BadgeMode => Kind switch
    {
        CardKind.Missing => BadgeMode.Missing,
        CardKind.NoResponse => BadgeMode.NoResponse,
        CardKind.File => BadgeMode.File,
        _ when !CountsLoaded => BadgeMode.Pending,
        _ when _errorBadge is not null => BadgeMode.Error,
        _ => BadgeMode.Folder,
    };

    public bool ShowFolderCount => CountsLoaded && _folderCount > 0;

    /// <summary>Zero counts are hidden; when both are zero "📄 0" is shown (§6.5).</summary>
    public bool ShowFileCount => CountsLoaded && (_fileCount > 0 || _folderCount == 0);

    public string FolderCountText => _folderCount.ToString("N0");

    public string FileCountText => _fileCount.ToString("N0");

    public string? ErrorBadgeText => _errorBadge;

    public string ExtensionText => Extension.Length > 1 ? Extension[1..].ToUpperInvariant() : "ファイル";

    public string? SizeText => Length is { } l ? Formatting.FileSize(l) : null;

    public string ActionText => Kind switch
    {
        CardKind.Folder or CardKind.Checking => "クリックでこのフォルダに移動",
        CardKind.File => "クリックで開く",
        CardKind.Missing => "フォルダが見つかりません",
        _ => "応答がありません",
    };

    public string ToolTipText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Path).Append('\n').Append(ActionText);
            if (LastUsed is { } used && OwnerTab != TabKind.Registered && IsTopLevel)
            {
                sb.Append('\n').Append("最終利用: ").Append(Formatting.DateTimeText(used));
            }

            return sb.ToString();
        }
    }

    public int FolderCount => _folderCount;

    public int FileCount => _fileCount;

    public ItemRef ToItemRef() => new(Path, IsFolderLike, Target, Context, this);

    /// <summary>The card became visible (or its container was recycled onto it).</summary>
    public void Realize(int thumbPx, int iconPx)
    {
        _thumbPx = thumbPx;
        _iconPx = iconPx;
        if (++_realized != 1)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token);
    }

    public void Unrealize()
    {
        if (_realized == 0 || --_realized != 0)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Thumbnail = null;
        ThumbMode = ThumbMode.None;
        Listing = null;
    }

    /// <summary>The card got wider than its thumbnail bucket: fetch a larger one.</summary>
    public void UpdateThumbSize(int thumbPx, int iconPx)
    {
        if (thumbPx <= _thumbPx || _realized == 0)
        {
            _thumbPx = Math.Max(_thumbPx, thumbPx);
            return;
        }

        _thumbPx = thumbPx;
        _iconPx = iconPx;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token);
    }

    /// <summary>Makes sure <see cref="Listing"/> is available (used when the overlay opens).</summary>
    public async Task<FolderListing?> EnsureListingAsync(CancellationToken cancellationToken)
    {
        if (Listing is not null)
        {
            return Listing;
        }

        if (!IsFolderLike)
        {
            return null;
        }

        var listing = await _services.Listings.GetAsync(Path, cancellationToken);
        ApplyListing(listing);
        return listing;
    }

    public void SetKind(CardKind kind) => Kind = kind;

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            if (Kind == CardKind.Checking && ExistenceTask is { } existence)
            {
                await existence.WaitAsync(ct);
            }

            if (IsDisabled)
            {
                return;
            }

            if (IsFolderLike)
            {
                await LoadFolderAsync(ct);
            }
            else
            {
                await LoadFileAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Card details failed for {Path}", ex);
        }
    }

    private async Task LoadFolderAsync(CancellationToken ct)
    {
        if (Listing is null || !_coverSearched)
        {
            await _services.DetailGate.WaitAsync(ct);
            try
            {
                var listing = Listing ?? await _services.Listings.GetAsync(Path, ct);
                ct.ThrowIfCancellationRequested();
                ApplyListing(listing);
                if (!_coverSearched)
                {
                    _coverPath = await FindCoverAsync(listing, ct);
                    _coverSearched = true;
                }
            }
            finally
            {
                _services.DetailGate.Release();
            }
        }

        ThumbResult result = ThumbResult.Empty;
        if (_coverPath is not null)
        {
            result = await _services.Thumbnails.GetAsync(_coverPath, ThumbSource.CoverImage, _thumbPx, _iconPx, default, 0, ct);
        }

        if (result.Image is null)
        {
            result = await _services.Thumbnails.GetAsync(Path, ThumbSource.Folder, _thumbPx, _iconPx, ModifiedUtc, 0, ct);
        }

        ct.ThrowIfCancellationRequested();
        Thumbnail = result.Image;
        ThumbMode = result.Mode;
    }

    private async Task LoadFileAsync(CancellationToken ct)
    {
        if (Length is null)
        {
            var entry = await Task.Run(() => _services.FileSystem.GetEntry(Path), ct);
            ct.ThrowIfCancellationRequested();
            if (entry is { } e)
            {
                Length = e.Length;
                ModifiedUtc = e.LastWriteTimeUtc;
                OnPropertyChanged(nameof(SizeText));
            }
        }

        var result = await _services.Thumbnails.GetAsync(Path, ThumbSource.File, _thumbPx, _iconPx, ModifiedUtc, Length ?? 0, ct);
        ct.ThrowIfCancellationRequested();
        Thumbnail = result.Image;
        ThumbMode = result.Mode;
    }

    private async Task<string?> FindCoverAsync(FolderListing listing, CancellationToken ct)
    {
        if (!listing.IsOk)
        {
            return null;
        }

        if (_services.Covers.TryGet(Path, out string cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        bool hidden = _services.ShowHidden;
        string? cover = await Task.Run(() => CoverFinder.Find(_services.FileSystem, Path, listing.Entries, hidden, ct), ct);
        _services.Covers.Set(Path, cover ?? string.Empty);
        return cover;
    }

    private void ApplyListing(FolderListing listing)
    {
        Listing = listing;
        _folderCount = listing.FolderCount;
        _fileCount = listing.FileCount;
        _errorBadge = listing.IsOk ? null : listing.StatusMessage;
        if (CountsLoaded)
        {
            OnPropertyChanged(nameof(BadgeMode));
            OnPropertyChanged(nameof(ShowFolderCount));
            OnPropertyChanged(nameof(ShowFileCount));
            OnPropertyChanged(nameof(FolderCountText));
            OnPropertyChanged(nameof(FileCountText));
        }
        else
        {
            CountsLoaded = true;
        }

        OnPropertyChanged(nameof(ErrorBadgeText));
        DetailsLoaded?.Invoke(this);
    }
}
