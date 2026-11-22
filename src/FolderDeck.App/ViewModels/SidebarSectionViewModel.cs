using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FolderDeck.ViewModels;

/// <summary>
/// One collapsible section of the sidebar: its header (the title; a click folds or unfolds the
/// section) and whether its rows are shown. What the rows are is the view's business.
/// </summary>
public sealed partial class SidebarSectionViewModel : ObservableObject
{
    private readonly Action<SidebarSectionViewModel> _changed;

    /// <summary>The rows are shown (false: only the header).</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <param name="id">Id in <c>sidebar_collapsed_sections</c> (<c>SidebarSections</c>).</param>
    /// <param name="title">Header text.</param>
    /// <param name="isExpanded">The saved state (not reported to <paramref name="changed"/>).</param>
    /// <param name="changed">Called after the user folded or unfolded the section.</param>
    public SidebarSectionViewModel(string id, string title, bool isExpanded, Action<SidebarSectionViewModel> changed)
    {
        Id = id;
        Title = title;
        _isExpanded = isExpanded;
        _changed = changed;
    }

    public string Id { get; }

    public string Title { get; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => _changed(this);
}
