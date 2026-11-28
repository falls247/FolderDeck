using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Layout;
using FolderDeck.Views;

namespace FolderDeck.ViewModels;

/// <summary>
/// A tab of the tab strip: how its panes (<see cref="PaneViewModel"/>) are laid out
/// (<see cref="Layout"/>, Core's <see cref="PaneLayout{T}"/>) and which one the toolbar, the address
/// bar, the keys and the menus act on (<see cref="ActivePane"/>). A tab that is not split has one
/// pane. What a tab shows on the strip (its title, its kind, its folder) is its active pane's.
/// Only folder tabs are split: fixed tabs (最近の履歴 / よく使う項目) and the placeholder are one
/// tab each for the whole session and keep their one pane. Only <c>MainViewModel</c> changes the
/// layout and the active pane (it mirrors the active pane's view on the toolbar and announces
/// its own <c>ActivePane</c> with it).
/// </summary>
public sealed partial class TabLayoutViewModel : ObservableObject, ICascadeAnchor
{
    public TabLayoutViewModel(PaneViewModel pane)
        : this(PaneLayout<PaneViewModel>.Single(pane), pane)
    {
    }

    public TabLayoutViewModel(PaneLayout<PaneViewModel> layout, PaneViewModel active)
    {
        if (!layout.Contains(active))
        {
            throw new ArgumentException("The active pane is not in the layout.", nameof(active));
        }

        Layout = layout;
        ActivePane = active;
        foreach (var pane in layout.Panes)
        {
            pane.PropertyChanged += OnPanePropertyChanged; // removed when the pane leaves the layout (SetLayout)
        }
    }

    public PaneLayout<PaneViewModel> Layout { get; private set; }

    /// <summary>The pane the toolbar, the address bar, the keys and the menus act on.</summary>
    public PaneViewModel ActivePane { get; private set; }

    /// <summary>Every pane of the tab, in reading order (left before right, top before bottom).</summary>
    public IReadOnlyList<PaneViewModel> Panes => Layout.Panes;

    /// <summary>The tab has more than one pane.</summary>
    public bool IsSplit => Layout.Panes.Count > 1;

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

    /// <summary>
    /// Replaces the layout (a split, a closed pane, a moved splitter) and the active pane, which must
    /// be in it. Panes that left the layout are no longer watched; the caller cancels their loads.
    /// </summary>
    internal void SetLayout(PaneLayout<PaneViewModel> layout, PaneViewModel active)
    {
        if (!layout.Contains(active))
        {
            throw new ArgumentException("The active pane is not in the layout.", nameof(active));
        }

        var old = Layout;
        foreach (var pane in old.Panes.Where(p => !layout.Contains(p)))
        {
            pane.PropertyChanged -= OnPanePropertyChanged;
        }

        foreach (var pane in layout.Panes.Where(p => !old.Contains(p)))
        {
            pane.PropertyChanged += OnPanePropertyChanged;
        }

        Layout = layout;
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(Panes));
        OnPropertyChanged(nameof(IsSplit));
        Activate(active);
    }

    /// <summary>Makes <paramref name="pane"/> (a pane of this tab) the active one.</summary>
    internal void Activate(PaneViewModel pane)
    {
        if (!Layout.Contains(pane))
        {
            throw new ArgumentException("The pane is not in the layout.", nameof(pane));
        }

        if (ReferenceEquals(pane, ActivePane))
        {
            return;
        }

        ActivePane = pane;
        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTipText));
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActivePane) && e.PropertyName is nameof(PaneViewModel.Title) or nameof(PaneViewModel.ToolTipText))
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
