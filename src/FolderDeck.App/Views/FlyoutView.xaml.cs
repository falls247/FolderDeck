using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FolderDeck.Views;

public partial class FlyoutView : UserControl
{
    /// <summary>Transparent margin around the visible card (room for the shadow), in DIP.</summary>
    public const double ShadowMargin = 16;

    public const double CardWidth = 280;

    public FlyoutView()
    {
        InitializeComponent();
        AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => e.Handled = true), handledEventsToo: true);
        Header.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            HeaderClicked?.Invoke(this);
        };
        Header.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            HeaderContextMenu?.Invoke(this);
        };
    }

    public event Action<FlyoutView>? HeaderClicked;

    public event Action<FlyoutView>? HeaderContextMenu;

    public EntryListView List => Rows;

    /// <summary>The visible card (without the shadow margin).</summary>
    public FrameworkElement CardElement => Card;

    public FrameworkElement HeaderElement => Header;

    public double CardMaxHeight
    {
        get => Card.MaxHeight;
        set => Card.MaxHeight = value;
    }
}
