using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Drives;
using FolderDeck.Core.Paths;
using FolderDeck.Shell;
using FolderDeck.Views;

namespace FolderDeck.ViewModels;

public enum SidebarItemState
{
    /// <summary>The existence check (a drive: the probe) is still running; the row looks usable.</summary>
    Checking,
    Ok,

    /// <summary>The folder no longer exists (§6.3). A drive: it is not ready (a network drive that is not connected).</summary>
    Missing,

    /// <summary>The existence check (a drive: the probe) took longer than 2 s (§6.3).</summary>
    NoResponse,
}

/// <summary>
/// One row of the sidebar (name only): a 登録フォルダ, or a drive of the PC section
/// (<see cref="ForDrive"/>). Resting on it opens the cascade, a click opens the folder in a new tab.
/// A drive row differs in what it is called (<see cref="Name"/> follows the volume label), in its
/// icon (<see cref="Glyph"/>) and in what its menu offers (<see cref="ItemContext.Drive"/>); it is
/// never dragged.
/// <para>
/// As an <see cref="ICascadeAnchor"/> it is the same for both kinds: the folders of its cascade open
/// in a tab of their own, and Ctrl / Shift mean nothing on a sidebar row.
/// </para>
/// </summary>
public sealed partial class SidebarItemViewModel : ObservableObject, ICascadeAnchor
{
    // Segoe Fluent Icons: hard drive, USB stick, disc, network drive.
    private const string FixedGlyph = "\uEDA2";
    private const string RemovableGlyph = "\uE88E";
    private const string OpticalGlyph = "\uE958";
    private const string NetworkGlyph = "\uE8CE";

    private readonly IconCache _icons;
    private readonly DriveKind? _driveKind;
    private ImageSource? _icon;
    private bool _iconRequested;

    /// <summary>Shown name. Fixed for a 登録フォルダ; a drive's follows its label (<see cref="UpdateDrive"/>).</summary>
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled), nameof(ToolTipText))]
    private SidebarItemState _state = SidebarItemState.Checking;

    /// <summary>The cascade opened from this row is showing (the row stays highlighted).</summary>
    [ObservableProperty]
    private bool _isHoverHeld;

    /// <summary>The row is being dragged to a new position (drawn semi-transparent).</summary>
    [ObservableProperty]
    private bool _isDragged;

    /// <summary>
    /// A drag would drop into this folder or drive (set by MainWindow: IsMouseOver does not follow a drag).
    /// </summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public SidebarItemViewModel(string id, string path, IconCache icons)
        : this(id, path, PathTools.DisplayName(path), null, icons)
    {
    }

    private SidebarItemViewModel(string id, string path, string name, DriveKind? driveKind, IconCache icons)
    {
        _icons = icons;
        _driveKind = driveKind;
        _name = name;
        Id = id;
        Path = path;
        Target = new NavLocation(TabKind.Folder, PathTools.NormalizeOrSelf(path), string.Empty);
    }

    /// <summary>Registration id (registered_folders.json). A drive row: its root.</summary>
    public string Id { get; }

    /// <summary>The folder. A drive row: the root of the drive.</summary>
    public string Path { get; }

    /// <summary>Where the folder opens: the root of a folder tab.</summary>
    public NavLocation Target { get; }

    /// <summary>A drive of the PC section (not a 登録フォルダ).</summary>
    public bool IsDrive => _driveKind is not null;

    /// <summary>Icon of a drive row, by the kind of drive (a font glyph, not the shell's icon); null for a 登録フォルダ.</summary>
    public string? Glyph => _driveKind switch
    {
        DriveKind.Fixed => FixedGlyph,
        DriveKind.Removable => RemovableGlyph,
        DriveKind.Optical => OpticalGlyph,
        DriveKind.Network => NetworkGlyph,
        _ => null,
    };

    /// <summary>
    /// Missing or not responding (a drive: not connected, not ready or not responding): no cascade,
    /// no tab. A 登録フォルダ can still be unregistered and moved.
    /// </summary>
    public bool IsDisabled => State is SidebarItemState.Missing or SidebarItemState.NoResponse;

    public bool OpensInNewTab => true;

    public bool YieldsToModifiers => false;

    public string ToolTipText => State switch
    {
        SidebarItemState.Missing when _driveKind == DriveKind.Network => Path + "\n接続されていません",
        SidebarItemState.Missing when IsDrive => Path + "\nドライブを使用できません",
        SidebarItemState.Missing => Path + "\nフォルダが見つかりません",
        SidebarItemState.NoResponse => Path + "\n応答がありません",
        _ => Path + "\nクリックで新しいタブに開く",
    };

    /// <summary>
    /// The generic folder icon (shared by every folder), loaded on first use. Always null for a
    /// drive row, which draws <see cref="Glyph"/> instead.
    /// </summary>
    public ImageSource? Icon
    {
        get
        {
            if (IsDrive)
            {
                return null;
            }

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

    /// <summary>
    /// The row of a drive, named as Explorer names it (<see cref="DriveRules.DisplayName"/>). Its
    /// state stays <see cref="SidebarItemState.Checking"/> until the caller has probed the drive.
    /// </summary>
    public static SidebarItemViewModel ForDrive(DriveEntry drive, IconCache icons) =>
        new(drive.Root, drive.Root, DriveRules.DisplayName(drive), drive.Kind, icons);

    /// <summary>A drive row takes the name of what the drive is now (a label that was read or changed, the mapped path).</summary>
    public void UpdateDrive(DriveEntry drive)
    {
        if (IsDrive)
        {
            Name = DriveRules.DisplayName(drive);
        }
    }

    public ItemRef ToItemRef() =>
        new(Path, true, Target, IsDrive ? ItemContext.Drive : ItemContext.Sidebar) { NewTab = true, IsDisabled = IsDisabled };

    private async Task LoadIconAsync()
    {
        _icon = await _icons.GetAsync(Path, isFolder: true);
        OnPropertyChanged(nameof(Icon));
    }
}
