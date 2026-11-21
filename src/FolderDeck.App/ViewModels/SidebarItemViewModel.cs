using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Paths;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

public enum SidebarItemState
{
    /// <summary>The existence check is still running (looks like a folder).</summary>
    Checking,
    Ok,

    /// <summary>The folder no longer exists (§6.3).</summary>
    Missing,

    /// <summary>The existence check took longer than 2 s (§6.3).</summary>
    NoResponse,
}

/// <summary>
/// One 登録フォルダ: a single row of the sidebar (name only). Resting on it opens the cascade, a
/// click opens the folder in a new tab.
/// </summary>
public sealed partial class SidebarItemViewModel : ObservableObject
{
    private readonly IconCache _icons;
    private ImageSource? _icon;
    private bool _iconRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled), nameof(ToolTipText))]
    private SidebarItemState _state = SidebarItemState.Checking;

    /// <summary>The cascade opened from this row is showing (the row stays highlighted).</summary>
    [ObservableProperty]
    private bool _isHoverHeld;

    /// <summary>The row is being dragged to a new position (drawn semi-transparent).</summary>
    [ObservableProperty]
    private bool _isDragged;

    public SidebarItemViewModel(string id, string path, IconCache icons)
    {
        _icons = icons;
        Id = id;
        Path = path;
        Name = PathTools.DisplayName(path);
        Target = new NavLocation(TabKind.Folder, PathTools.NormalizeOrSelf(path), string.Empty);
    }

    /// <summary>Registration id (registered_folders.json).</summary>
    public string Id { get; }

    public string Path { get; }

    public string Name { get; }

    /// <summary>Where the folder opens: the root of a folder tab.</summary>
    public NavLocation Target { get; }

    /// <summary>Missing or not responding: no cascade, no tab; it can still be unregistered and moved.</summary>
    public bool IsDisabled => State is SidebarItemState.Missing or SidebarItemState.NoResponse;

    public string ToolTipText => State switch
    {
        SidebarItemState.Missing => Path + "\nフォルダが見つかりません",
        SidebarItemState.NoResponse => Path + "\n応答がありません",
        _ => Path + "\nクリックで新しいタブに開く",
    };

    /// <summary>The generic folder icon (shared by every folder), loaded on first use.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                if (_icons.TryGet(Path, isFolder: true, out var cached))
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

    public ItemRef ToItemRef() => new(Path, true, Target, ItemContext.Sidebar) { NewTab = true };

    private async Task LoadIconAsync()
    {
        _icon = await _icons.GetAsync(Path, isFolder: true);
        OnPropertyChanged(nameof(Icon));
    }
}
