using CommunityToolkit.Mvvm.ComponentModel;

namespace FolderDeck.ViewModels;

/// <summary>
/// A row of the sidebar section 登録ペイン: one saved pane layout of registered_panes.json. Not a folder
/// row (no cascade, no existence check, no drag): a click opens the layout, the right click its menu.
/// </summary>
public sealed partial class RegisteredPaneItemViewModel(string id) : ObservableObject
{
    /// <summary>The entry's id in registered_panes.json (rows are matched by it when the store changes).</summary>
    public string Id { get; } = id;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>What the layout holds, one line per pane (the row's tooltip).</summary>
    [ObservableProperty]
    private string _toolTipText = string.Empty;

    /// <summary>The pane group on screen was opened from (or registered as) this entry: the row is highlighted.</summary>
    [ObservableProperty]
    private bool _isCurrent;
}
