using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Views;

namespace FolderDeck.ViewModels;

/// <summary>
/// A tab of the tab strip and the one pane (<see cref="PaneViewModel"/>) that holds what it shows:
/// its location, cards, selection, view and history. (The name is from a first design in which a
/// tab held a layout of panes; the split now belongs to the window: <c>MainViewModel.ShownLayout</c>,
/// whose leaves are the panes of the tabs on screen.) What a tab shows on the strip (its title, its
/// kind, its folder) is its pane's. Fixed tabs (最近の履歴 / よく使う項目) and the placeholder are
/// one tab each for the whole session.
/// </summary>
public sealed partial class TabLayoutViewModel : ObservableObject, ICascadeAnchor
{
    public TabLayoutViewModel(PaneViewModel pane)
    {
        ActivePane = pane;
        // The pane lives as long as its tab: nothing to unsubscribe.
        pane.PropertyChanged += OnPanePropertyChanged;
    }

    /// <summary>The tab's pane (one per tab, for the tab's whole life).</summary>
    public PaneViewModel ActivePane { get; }

    public TabKind Kind => ActivePane.Kind;

    public bool IsFolderTab => ActivePane.IsFolderTab;

    /// <summary>The stand-in that is selected while no tab is open (<see cref="TabKind.None"/>).</summary>
    public bool IsPlaceholder => ActivePane.IsPlaceholder;

    /// <summary>Tab strip text: the pane's (<see cref="PaneViewModel.Title"/>).</summary>
    public string Title => ActivePane.Title;

    /// <summary>The folder the pane shows (null at a top level).</summary>
    public string? ToolTipText => ActivePane.ToolTipText;

    /// <summary>The tab is the one shown (tab strip highlight); while the window is split, the one in the active pane.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The tab is on the tab strip. Set by <c>MainViewModel</c> together with its <c>Tabs</c>.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// While this tab is in a pane group (on screen or not): its pane's colour (0 to
    /// <c>PaneRules.MaxPanes</c> − 1), the same on the tab and on the pane; −1 otherwise. Kept while the
    /// tab stays in its group (set by <c>MainViewModel.UpdatePaneSlots</c>).
    /// </summary>
    [ObservableProperty]
    private int _paneSlot = -1;

    /// <summary>The tab is in the pane group on screen (its content is in a pane now).</summary>
    [ObservableProperty]
    private bool _isOnScreen;

    /// <summary>The tab is the first (<see cref="IsGroupStart"/>) / last (<see cref="IsGroupEnd"/>) of its pane group on the strip (a gap marks the group).</summary>
    [ObservableProperty]
    private bool _isGroupStart;

    [ObservableProperty]
    private bool _isGroupEnd;

    /// <summary>The tab is in a pane group (on screen or not): it has a pane colour.</summary>
    public bool IsGrouped => PaneSlot >= 0;

    partial void OnPaneSlotChanged(int value) => OnPropertyChanged(nameof(IsGrouped));

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
    /// Nothing to list: a tab on screen (the shown tab, or one in a pane of the split: its content is
    /// there), the top list of 最近の履歴 / よく使う項目, an unknown root, or a tab whose last load
    /// failed (its banner says why; it is read again when shown).
    /// </summary>
    bool ICascadeAnchor.IsDisabled => IsSelected || IsOnScreen || ActivePane.CurrentFolderPath is null || ActivePane.BannerText is not null;

    /// <summary>
    /// Outside the shown tab, as a sidebar row: picked folders open in a folder tab (an open tab of that
    /// folder is selected), and the list outlives the shown tab's content (<see cref="ICascadeAnchor.OutlivesContent"/>).
    /// </summary>
    bool ICascadeAnchor.OpensInNewTab => true;

    bool ICascadeAnchor.YieldsToModifiers => false;

    /// <summary>The strip is at the top: the list goes under the header (files too: a look into the tab).</summary>
    bool ICascadeAnchor.OpensBelow => true;
}
