using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderDeck.Views;

/// <summary>
/// A see-through picture of what is being dragged inside the window (a tab, a pane group's tabs) that
/// follows the pointer, so the drag shows what it holds (a tab dragged onto the panes leaves the strip).
/// Drawn in the window's adorner layer, above everything and never hit by the pointer. A snapshot taken
/// when the drag starts: the tabs moving under it on the strip do not change it.
/// </summary>
internal sealed class DragGhost : Adorner
{
    private const double GhostOpacity = 0.6;

    private readonly ImageSource _image;
    private readonly Size _size;

    /// <summary>Where the pointer holds the picture, from its top-left corner.</summary>
    private readonly Vector _grab;

    private Point _topLeft;

    private DragGhost(UIElement root, ImageSource image, Size size, Vector grab) : base(root)
    {
        _image = image;
        _size = size;
        _grab = grab;
        IsHitTestVisible = false;
    }

    /// <summary>
    /// Starts a ghost of <paramref name="area"/> of <paramref name="source"/> (in its coordinates), held at
    /// <paramref name="grab"/> (in the same coordinates). Null when it cannot be drawn (no window, no size).
    /// </summary>
    public static DragGhost? Start(FrameworkElement source, Rect area, Point grab)
    {
        if (Window.GetWindow(source)?.Content is not UIElement root || AdornerLayer.GetAdornerLayer(root) is not { } layer
            || area.Width < 1 || area.Height < 1)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(source);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(source) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            dc.DrawRectangle(brush, null, new Rect(area.Size));
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(area.Width * dpi.DpiScaleX), (int)Math.Ceiling(area.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var ghost = new DragGhost(root, bitmap, area.Size, grab - area.TopLeft);
        ghost._topLeft = source.TranslatePoint(area.TopLeft, root);
        layer.Add(ghost);
        return ghost;
    }

    /// <summary>The pointer is at <paramref name="screen"/> (screen coordinates, as the drag reports them).</summary>
    public void MoveTo(Point screen)
    {
        if (PresentationSource.FromVisual(AdornedElement) is null)
        {
            return;
        }

        _topLeft = AdornedElement.PointFromScreen(screen) - _grab;
        InvalidateVisual();
    }

    public void End() => AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.PushOpacity(GhostOpacity);
        drawingContext.DrawImage(_image, new Rect(_topLeft, _size));
        drawingContext.Pop();
    }
}
