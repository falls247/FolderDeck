using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// What UI Automation sees of a card or a row of the details list: one list item with the item's
/// name, and nothing inside it (no peers for its texts, image, 📌 or badges).
/// <para>
/// Why: while any UI Automation client listens (screen readers, IME helpers, automation tools),
/// WPF walks the whole automation tree after every layout pass and raises property changes for
/// each new peer. A folder with thousands of items made that thousands of peers per pass (busy
/// for seconds), and one such raise inside a layout that the IME started (TSF) never returned:
/// the app hung. One peer per item keeps the walk small; the names stay readable.
/// </para>
/// </summary>
/// <remarks>
/// Nothing that changes while the item is shown (counts, the thumbnail's state, errors) goes into
/// its Name or ItemStatus: each change would be one more automation event per card.
/// </remarks>
internal sealed class ItemViewAutomationPeer : FrameworkElementAutomationPeer
{
    public ItemViewAutomationPeer(FrameworkElement owner)
        : base(owner)
    {
    }

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override string GetNameCore()
    {
        string name = base.GetNameCore(); // AutomationProperties.Name, when set
        return name.Length > 0 ? name : ((FrameworkElement)Owner).DataContext is CardViewModel vm ? vm.Name : string.Empty;
    }
}

/// <summary>
/// The cards of one row of the grid. An ItemsControl's own automation peer makes an item peer per
/// card (and a new wrapper peer each time it is asked), all new whenever the row is rebuilt (a load,
/// a sort, a scroll that brings the row in); this one is a plain group whose children are the cards'
/// <see cref="ItemViewAutomationPeer"/> (found below the item containers, which have no peer).
/// </summary>
public sealed class CardRowItems : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PlainItemsPeer(this, AutomationControlType.Group, "CardRow");
}

/// <summary>
/// The rows of the details list, for the same reason as <see cref="CardRowItems"/>: a plain list whose
/// children are the shown rows' <see cref="ItemViewAutomationPeer"/> (under the scroll viewer), without
/// an item peer per row that is new whenever the rows are rebuilt.
/// </summary>
public sealed class DetailsRowItems : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PlainItemsPeer(this, AutomationControlType.List, "DetailsRows");
}

/// <summary>
/// An items control seen as a plain element: its children come from the visual tree (the item views'
/// own peers), not from its items. Not Custom: WPF walks a Custom peer's children again after every
/// layout pass.
/// </summary>
internal sealed class PlainItemsPeer(FrameworkElement owner, AutomationControlType type, string className) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => type;

    protected override string GetClassNameCore() => className;
}
