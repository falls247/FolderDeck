using System.IO;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// What a cascade that does not hang on a card is opened from (<see cref="CascadeController.AnchorMouseEnter"/>):
/// a folder whose list opens in a flyout beside an element of the window. A sidebar row (登録フォルダ,
/// drive) is one, a folder row of the details list (<see cref="CardAnchor"/>) another, a crumb of the
/// address bar (<see cref="BreadcrumbItem"/>, its list under it) a third; the element
/// itself is passed separately, so one item can be shown by any view.
/// </summary>
public interface ICascadeAnchor
{
    /// <summary>The folder whose entries the first list shows (for a file anchor, the file).</summary>
    string Path { get; }

    /// <summary>
    /// Where that folder is, as a location (the header of the list and its rows navigate from it).
    /// Null for a file anchor (<see cref="File"/> is not null).
    /// </summary>
    NavLocation? Target { get; }

    /// <summary>
    /// When not null, resting on the element shows the preview of this file beside it instead of a
    /// list. <see cref="Path"/> is then the file's path and <see cref="Target"/> is null.
    /// </summary>
    AnchorFile? File => null;

    /// <summary>Missing or not responding: resting on it opens nothing.</summary>
    bool IsDisabled { get; }

    /// <summary>
    /// Set by the controller while the list beside the item is showing, so that the item stays
    /// highlighted when the pointer is in the list. The controller clears it on every close.
    /// </summary>
    bool IsHoverHeld { get; set; }

    /// <summary>
    /// True when the item is outside the shown tab (a sidebar row): folders picked in its cascade
    /// open in a folder tab of their own, and the cascade outlives changes of the tab's content.
    /// False when it is an item of the tab: folders navigate inside the tab and the cascade closes
    /// with the content (<see cref="CascadeController.CloseCardCascade"/>).
    /// </summary>
    bool OpensInNewTab { get; }

    /// <summary>
    /// True when Ctrl or Shift on the item means selecting, so its list must stay out of the way
    /// while one of them is down. Never true for a sidebar row.
    /// </summary>
    bool YieldsToModifiers { get; }

    /// <summary>
    /// True when the first list opens under the element (a crumb of the address bar) instead of
    /// beside it. Deeper lists still open beside their parent list.
    /// </summary>
    bool OpensBelow => false;

    /// <summary>True when every list of the cascade shows folders only (a crumb: the user picks where to go).</summary>
    bool FoldersOnly => false;

    /// <summary>
    /// True when the cascade does not hang on the tab's content, so changes of that content
    /// (<see cref="CascadeController.CloseCardCascade"/>) leave it open: a sidebar row
    /// (<see cref="OpensInNewTab"/>) and a crumb of the address bar, whose folders open inside the tab.
    /// </summary>
    bool OutlivesContent => OpensInNewTab;
}

/// <summary>
/// The file an anchor stands for instead of a folder: <see cref="Row"/> is what the preview is made
/// from, <see cref="Card"/> what its click and menu act on.
/// </summary>
public sealed record AnchorFile(EntryRowViewModel Row, CardViewModel Card);

/// <summary>
/// A folder or previewable file of the shown tab as a cascade anchor: what the details list passes
/// for a row, whose name is the element the list (or the preview) hangs beside. (A card needs none:
/// its list is an overlay on the card itself.) Folders picked in the cascade navigate inside the
/// tab, as from a card, except under a top-level card (<see cref="IsTopLevel"/>).
/// </summary>
public sealed class CardAnchor : ICascadeAnchor
{
    private readonly CardViewModel _card;
    private readonly IconCache _icons;
    private AnchorFile? _file;

    private CardAnchor(CardViewModel card, NavLocation? target, IconCache icons)
    {
        _card = card;
        Target = target;
        _icons = icons;
    }

    public string Path => _card.Path;

    public NavLocation? Target { get; }

    /// <summary>The row the preview is made from, created on first use (<see cref="For"/> runs on every mouse move).</summary>
    public AnchorFile? File => _card.IsFile ? _file ??= new AnchorFile(Row(), _card) : null;

    public bool IsDisabled => _card.IsDisabled;

    /// <summary>The row stays highlighted while its list is open: the flag lives on the item, so any view of it shows it.</summary>
    public bool IsHoverHeld
    {
        get => _card.IsHoverHeld;
        set => _card.IsHoverHeld = value;
    }

    public bool OpensInNewTab => false;

    /// <summary>
    /// A row of the top level of 最近の履歴・よく使う項目: folders picked in its cascade open in a folder
    /// tab of their own (<see cref="CardViewModel.ToItemRef"/>). Unlike <see cref="OpensInNewTab"/>, the
    /// cascade still belongs to the tab's content and closes with it.
    /// </summary>
    public bool IsTopLevel => _card.IsTopLevel;

    /// <summary>The list covers the columns right of the name, where a Ctrl / Shift click selects.</summary>
    public bool YieldsToModifiers => true;

    /// <summary>
    /// The anchor of <paramref name="card"/>: a folder that can be navigated to, or a file that can
    /// be previewed (missing and not responding items are not files). Null for anything else.
    /// A file of unknown size is left out, so that the preview caches are not keyed on a wrong length.
    /// </summary>
    public static CardAnchor? For(CardViewModel card, IconCache icons)
    {
        if (card is { IsFolderLike: true, Target: { } target })
        {
            return new CardAnchor(card, target, icons);
        }

        return card is { IsFile: true, Length: not null } && ShellPreviewSupport.IsPreviewable(card.Name)
            ? new CardAnchor(card, null, icons)
            : null;
    }

    private EntryRowViewModel Row() =>
        // Files never navigate: ToItemRef gives them no target, so the location is a placeholder.
        new(
            new FsEntry(_card.Name, _card.Path, false, false, _card.Length ?? 0, _card.ModifiedUtc, FileAttributes.Normal),
            new NavLocation(_card.Tab, _card.Path, string.Empty),
            _icons);
}
