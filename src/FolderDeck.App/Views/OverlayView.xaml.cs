using System.Windows.Controls;
using System.Windows.Input;

namespace FolderDeck.Views;

public partial class OverlayView : UserControl
{
    public OverlayView()
    {
        InitializeComponent();

        // The wheel scrolls only this list, never the grid behind it, even at the ends (§9.2).
        AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => e.Handled = true), handledEventsToo: true);

        // Clicking the header or empty space does nothing (it must not open the card).
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => e.Handled = true;
        MouseRightButtonUp += (_, e) => e.Handled = true;
    }

    public EntryListView List => Rows;
}
