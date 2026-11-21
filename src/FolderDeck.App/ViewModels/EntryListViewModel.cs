using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Text;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

/// <summary>One row of the overlay or a flyout (§9.4).</summary>
public sealed partial class EntryRowViewModel : ObservableObject
{
    private readonly IconCache _icons;
    private ImageSource? _icon;
    private bool _iconRequested;
    private string? _toolTip;
    private bool? _isPreviewable;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isKeySelected;

    public EntryRowViewModel(FsEntry entry, NavLocation target, IconCache icons)
    {
        _icons = icons;
        Name = entry.Name;
        Path = entry.FullPath;
        IsFolder = entry.IsDirectory;
        Length = entry.Length;
        ModifiedUtc = entry.LastWriteTimeUtc;
        Target = target;
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsFolder { get; }

    public long Length { get; }

    public DateTime ModifiedUtc { get; }

    /// <summary>
    /// An image, Office file, text file, or a file Windows draws a thumbnail of: resting on the row
    /// shows a preview beside the list. Decided on first use (the shell is asked once per extension).
    /// </summary>
    public bool IsPreviewable => _isPreviewable ??= !IsFolder && ShellPreviewSupport.IsPreviewable(Name);

    /// <summary>How the preview is made (<see cref="PreviewKind.None"/> = the shell's thumbnail).</summary>
    public PreviewKind PreviewKind => IsFolder ? PreviewKind.None : PreviewKinds.KindOf(Name);

    /// <summary>Location of this row inside the app (folder rows navigate here).</summary>
    public NavLocation Target { get; }

    /// <summary>Loaded lazily: only rows that the virtualized list realizes ask for an icon.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                if (_icons.TryGet(Path, IsFolder, out var cached))
                {
                    _icon = cached;
                }
                else
                {
                    _ = LoadIconAsync();
                }
            }

            return _icon;
        }
    }

    public string ToolTipText => _toolTip ??= IsFolder
        ? $"{Name}\nクリックでこのフォルダに移動"
        : $"{Name}\n{Formatting.FileSize(Length)} · {Formatting.DateTimeText(new DateTimeOffset(DateTime.SpecifyKind(ModifiedUtc, DateTimeKind.Utc)))}\nクリックで開く";

    public ItemRef ToItemRef() => new(Path, IsFolder, IsFolder ? Target : null, ItemContext.Row);

    private async Task LoadIconAsync()
    {
        var icon = await _icons.GetAsync(Path, IsFolder);
        _icon = icon;
        OnPropertyChanged(nameof(Icon));
    }
}

public enum ListState
{
    Loading,
    Ready,
    Empty,
    Error,
}

/// <summary>Contents of one cascade level: the overlay (level 0) or a flyout.</summary>
public sealed partial class EntryListViewModel : ObservableObject
{
    private readonly IconCache _icons;

    [ObservableProperty]
    private IReadOnlyList<EntryRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(ShowStatus), nameof(IsError))]
    private ListState _state = ListState.Loading;

    [ObservableProperty]
    private string _statusText = "読み込み中…";

    [ObservableProperty]
    private string _headerCounts = string.Empty;

    [ObservableProperty]
    private int _folderCount;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private bool _countsKnown;

    public EntryListViewModel(string path, NavLocation location, IconCache icons)
    {
        _icons = icons;
        Path = path;
        Location = location;
        Title = PathTools.DisplayName(path);
    }

    public string Path { get; }

    public string Title { get; }

    /// <summary>Where this folder is inside the app (the flyout header navigates here).</summary>
    public NavLocation Location { get; }

    public bool IsLoading => State == ListState.Loading;

    public bool IsError => State == ListState.Error;

    public bool ShowStatus => State != ListState.Ready;

    public bool ShowFolderCount => CountsKnown && FolderCount > 0;

    public bool ShowFileCount => CountsKnown && (FileCount > 0 || FolderCount == 0);

    public string ToolTipText => $"{Path}\nクリックでこのフォルダに移動";

    public void MarkSlow()
    {
        if (State == ListState.Loading)
        {
            StatusText = "応答がありません（待機中…）";
        }
    }

    public void Apply(FolderListing listing)
    {
        if (!listing.IsOk)
        {
            Rows = [];
            StatusText = listing.StatusMessage;
            State = ListState.Error;
            return;
        }

        var rows = new EntryRowViewModel[listing.Entries.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var e = listing.Entries[i];
            rows[i] = new EntryRowViewModel(e, Location with { Rel = PathTools.JoinRel(Location.Rel, e.Name) }, _icons);
        }

        FolderCount = listing.FolderCount;
        FileCount = listing.FileCount;
        CountsKnown = true;
        HeaderCounts = $"フォルダ {listing.FolderCount:N0} / ファイル {listing.FileCount:N0}";
        OnPropertyChanged(nameof(ShowFolderCount));
        OnPropertyChanged(nameof(ShowFileCount));
        Rows = rows;
        if (rows.Length == 0)
        {
            StatusText = "空のフォルダ";
            State = ListState.Empty;
        }
        else
        {
            State = ListState.Ready;
        }
    }
}
