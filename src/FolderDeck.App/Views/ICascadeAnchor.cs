using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// What a cascade that does not hang on a card is opened from (<see cref="CascadeController.AnchorMouseEnter"/>):
/// a folder whose list opens in a flyout beside an element of the window. A sidebar row (登録フォルダ,
/// drive) is one, a folder row of the details list (<see cref="CardAnchor"/>) another; the element
/// itself is passed separately, so one item can be shown by any view.
/// </summary>
public interface ICascadeAnchor
{
    /// <summary>The folder whose entries the first list shows.</summary>
    string Path { get; }

    /// <summary>Where that folder is, as a location (the header of the list and its rows navigate from it).</summary>
    NavLocation Target { get; }

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
}

/// <summary>
/// A folder of the shown tab as a cascade anchor: what the details list passes for a row, whose
/// name is the element the list hangs beside. (A card needs none: its list is an overlay on the
/// card itself.) Folders picked in the cascade navigate inside the tab, as from a card.
/// </summary>
public sealed class CardAnchor : ICascadeAnchor
{
    private readonly CardViewModel _card;

    private CardAnchor(CardViewModel card, NavLocation target)
    {
        _card = card;
        Target = target;
    }

    public string Path => _card.Path;

    public NavLocation Target { get; }

    public bool IsDisabled => _card.IsDisabled;

    /// <summary>The row stays highlighted while its list is open: the flag lives on the item, so any view of it shows it.</summary>
    public bool IsHoverHeld
    {
        get => _card.IsHoverHeld;
        set => _card.IsHoverHeld = value;
    }

    public bool OpensInNewTab => false;

    /// <summary>The list covers the columns right of the name, where a Ctrl / Shift click selects.</summary>
    public bool YieldsToModifiers => true;

    /// <summary>The anchor of <paramref name="card"/>, or null when it is not a folder that can be navigated to.</summary>
    public static CardAnchor? For(CardViewModel card) =>
        card is { IsFolderLike: true, Target: { } target } ? new CardAnchor(card, target) : null;
}
