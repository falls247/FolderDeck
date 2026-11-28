using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Layout;
using FolderDeck.Views;

namespace FolderDeck.ViewModels;

/// <summary>
/// A tab of the tab strip: how its panes (<see cref="PaneViewModel"/>) are laid out
/// (<see cref="Layout"/>) and which one the toolbar, the address bar, the keys and the menus act on
/// (<see cref="ActivePane"/>). A tab that is not split has one pane; so does every tab for now.
/// What a tab shows (its title, its kind, its folder) is its active pane's. Fixed tabs
/// (最近の履歴 / よく使う項目) and the placeholder are one tab each for the whole session, and
/// never have more than their one pane.
/// </summary>
public sealed partial class TabLayoutViewModel : ObservableObject, ICascadeAnchor
{
    public TabLayoutViewModel(PaneViewModel pane)
    {
        Layout = PaneLayout<PaneViewModel>.Single(pane);
        ActivePane = pane;
        // The pane lives as long as its tab: nothing to unsubscribe.
        pane.PropertyChanged += OnPanePropertyChanged;
    }

    public PaneLayout<PaneViewModel> Layout { get; }

    /// <summary>The pane the toolbar, the address bar, the keys and the menus act on.</summary>
    public PaneViewModel ActivePane { get; }

    /// <summary>Every pane of the tab, in reading order.</summary>
    public IReadOnlyList<PaneViewModel> Panes => Layout.Panes;

    public TabKind Kind => ActivePane.Kind;

    public bool IsFolderTab => ActivePane.IsFolderTab;

    /// <summary>The stand-in that is selected while no tab is open (<see cref="TabKind.None"/>).</summary>
    public bool IsPlaceholder => ActivePane.IsPlaceholder;

    /// <summary>Tab strip text: the active pane's (<see cref="PaneViewModel.Title"/>).</summary>
    public string Title => ActivePane.Title;

    /// <summary>The folder the active pane shows (null at a top level).</summary>
    public string? ToolTipText => ActivePane.ToolTipText;

    /// <summary>The tab is the one shown (tab strip highlight).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The tab is on the tab strip. Set by <c>MainViewModel</c> together with its <c>Tabs</c>.</summary>
    [ObservableProperty]
    private bool _isOpen;

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.Title) or nameof(PaneViewModel.ToolTipText))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    // ---- cascade anchor: the header of a tab that is not shown (FolderTabStrip → CascadeController) ----

    /// <summary>Set by the cascade while the list under this tab's header is showing (the header stays highlighted, its tooltip off).</summary>
    [ObservableProperty]
    private bool _isHoverHeld;

    string ICascadeAnchor.Path => ActivePane.CurrentFolderPath ?? string.Empty;

    NavLocation? ICascadeAnchor.Target => ActivePane.CurrentFolderPath is null ? null : ActivePane.Location;

    /// <summary>
    /// Nothing to list: the shown tab (its content is on screen), the top list of 最近の履歴 / よく使う項目,
    /// an unknown root, or a tab whose last load failed (its banner says why; it is read again when shown).
    /// </summary>
    bool ICascadeAnchor.IsDisabled => IsSelected || ActivePane.CurrentFolderPath is null || ActivePane.BannerText is not null;

    /// <summary>
    /// Outside the shown tab, as a sidebar row: picked folders open in a folder tab (an open tab of that
    /// folder is selected), and the list outlives the shown tab's content (<see cref="ICascadeAnchor.OutlivesContent"/>).
    /// </summary>
    bool ICascadeAnchor.OpensInNewTab => true;

    bool ICascadeAnchor.YieldsToModifiers => false;

    /// <summary>The strip is at the top: the list goes under the header (files too: a look into the tab).</summary>
    bool ICascadeAnchor.OpensBelow => true;
}
