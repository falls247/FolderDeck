using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace FolderDeck.Views;

/// <summary>
/// The root of one layout's pane views in the window (<c>MainWindow.BuildPanes</c>): the one on
/// screen is visible, the ones kept for the tabs and groups shown before are collapsed. The host is
/// what is collapsed, never a pane view itself (a collapsed element reads a size of 0; the views
/// inside keep theirs, and their lists are not laid out again when the host is shown).
/// </summary>
internal sealed class PaneScreenHost : Decorator
{
    protected override AutomationPeer OnCreateAutomationPeer() => new HostPeer(this);

    /// <summary>
    /// Nothing of a hidden host is in the automation tree: WPF walks the whole tree after every layout
    /// pass while a client listens (see <see cref="ItemViewAutomationPeer"/>), and the kept views would
    /// add their items to each walk. Shown, the host is only a node of the raw view.
    /// </summary>
    private sealed class HostPeer : FrameworkElementAutomationPeer
    {
        public HostPeer(PaneScreenHost owner)
            : base(owner)
        {
        }

        protected override List<AutomationPeer>? GetChildrenCore() =>
            ((UIElement)Owner).Visibility == Visibility.Visible ? base.GetChildrenCore() : null;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetClassNameCore() => nameof(PaneScreenHost);
    }
}
