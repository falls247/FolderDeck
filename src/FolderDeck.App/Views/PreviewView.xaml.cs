using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FolderDeck.Views;

/// <summary>Preview popup content: name, size · date, and the image (or the presentation's cover).</summary>
public partial class PreviewView : UserControl
{
    /// <summary>Transparent margin around the visible card (room for the shadow), in DIP.</summary>
    public const double ShadowMargin = 16;

    public const double CardWidth = 320;

    /// <summary>Width available to the picture: card width − padding − border.</summary>
    public const double ImageWidth = CardWidth - 20 - 2;

    private bool _pressed;

    public PreviewView()
    {
        InitializeComponent();
        AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => e.Handled = true), handledEventsToo: true);
        Card.MouseLeftButtonDown += (_, e) => _pressed = e.ClickCount == 1;
        Card.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed)
            {
                e.Handled = true;
                _pressed = false;
                Clicked?.Invoke(this);
            }
        };
        Card.MouseLeave += (_, _) => _pressed = false;
        Card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ContextMenuRequested?.Invoke(this);
        };
    }

    /// <summary>Left click: open the file, like clicking its row.</summary>
    public event Action<PreviewView>? Clicked;

    public event Action<PreviewView>? ContextMenuRequested;

    /// <summary>The visible card (without the shadow margin).</summary>
    public FrameworkElement CardElement => Card;

    public double CardMaxHeight
    {
        get => Card.MaxHeight;
        set => Card.MaxHeight = value;
    }
}
