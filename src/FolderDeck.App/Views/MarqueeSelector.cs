using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FolderDeck.Core.Input;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// What a marquee tells the window: it really started (the pointer passed the drag distance), the
/// items in the box changed (all of them, also those scrolled out of view, in display order), and
/// it ended (cancelled with Esc or not; started or only pressed).
/// </summary>
public sealed record MarqueeHandlers(Action Started, Action<IReadOnlyList<CardViewModel>> BoxChanged, Action<bool, bool> Ended);

/// <summary>
/// Where the cells of an items view are, in the coordinates of its rows host (the surface):
/// <see cref="Origin"/> is where the content's (0, 0) lies (row 0, column 0, which may be scrolled
/// away), <see cref="Grid"/> the cell sizes and pitches measured from realized rows.
/// </summary>
internal readonly record struct MarqueeLayout(Point Origin, CellGrid Grid);

/// <summary>
/// The rubber-band selection of one items view (the card grid or the details list), from a press
/// on its blank part to the release: mouse capture on the view's scroll viewer (Element mode, so
/// the cards, rows and sidebar get no hover meanwhile; the wheel still scrolls), the box drawn in
/// an adorner, auto-scroll past the top or bottom edge, and the items in the box found by the
/// layout's geometry (<see cref="MarqueeGrid"/>) rather than by realized containers, so that a
/// virtualized list keeps what scrolled away selected.
/// </summary>
internal sealed class MarqueeSelector
{
    private const double EdgeInset = 8;
    private const double EdgeRamp = 64;
    private const double EdgeMaxStep = 24;

    private readonly ScrollViewer _scroll;
    private readonly FrameworkElement _surface;
    private readonly FrameworkElement _viewport;
    private readonly IReadOnlyList<CardViewModel> _items;
    private readonly Func<MarqueeLayout?> _measure;
    private readonly MarqueeHandlers _handlers;
    private readonly DispatcherTimer _timer;
    private readonly MarqueeAdorner _adorner;
    private readonly Point _press;
    private readonly Point _anchor;
    private Point? _lastOrigin;
    private double _lastOffset;
    private CellSpan? _span;
    private bool _started;
    private bool _done;

    private MarqueeSelector(
        ScrollViewer scroll, FrameworkElement surface, IReadOnlyList<CardViewModel> items,
        Func<MarqueeLayout?> measure, MarqueeHandlers handlers, Point press)
    {
        _scroll = scroll;
        _surface = surface;
        _viewport = FindDescendant<ScrollContentPresenter>(scroll) ?? (FrameworkElement)scroll;
        _items = items;
        _measure = measure;
        _handlers = handlers;
        _press = press;
        Point origin = Origin();
        _anchor = new Point(press.X - origin.X, press.Y - origin.Y);
        var accent = surface.TryFindResource("Brush.Accent") as SolidColorBrush ?? Brushes.DodgerBlue;
        _adorner = new MarqueeAdorner(surface, accent.Color);
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => AutoScrollTick();
    }

    /// <summary>Raised once when the marquee ends, before <see cref="MarqueeHandlers.Ended"/>: the view forgets it.</summary>
    public event Action? Finished;

    /// <summary>
    /// Starts a marquee for a left press <paramref name="e"/> on the blank part of
    /// <paramref name="scroll"/>'s viewport (not on an overlay above it, such as the
    /// "応答がありません" panel): takes the mouse capture and waits for the drag distance. Null when
    /// the press is elsewhere or the capture cannot be taken.
    /// </summary>
    public static MarqueeSelector? TryStart(
        ScrollViewer scroll, FrameworkElement surface, IReadOnlyList<CardViewModel> items,
        Func<MarqueeLayout?> measure, MouseButtonEventArgs e, MarqueeHandlers handlers)
    {
        if (e.OriginalSource is not Visual source
            || !(ReferenceEquals(source, scroll) || scroll.IsAncestorOf(source))
            || !ItemsViewHitTest.InViewport(scroll, e))
        {
            return null;
        }

        var marquee = new MarqueeSelector(scroll, surface, items, measure, handlers, e.GetPosition(surface));

        // Element, not SubTree: the cards and rows below must not see the pointer (hover, cascade).
        if (!Mouse.Capture(scroll, CaptureMode.Element))
        {
            return null;
        }

        scroll.MouseMove += marquee.OnMouseMove;
        scroll.PreviewMouseLeftButtonUp += marquee.OnLeftUp;
        scroll.LostMouseCapture += marquee.OnLostCapture;
        scroll.ScrollChanged += marquee.OnScrollChanged;
        return marquee;
    }

    /// <summary>Ends the marquee, keeping the selection it made.</summary>
    public void End() => Finish(cancelled: false);

    /// <summary>Ends the marquee; the window puts the selection back as it was at the press.</summary>
    public void Cancel() => Finish(cancelled: true);

    private void OnMouseMove(object sender, MouseEventArgs e) => Update(e.GetPosition(_surface));

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_started)
        {
            e.Handled = true; // not a click on whatever lies under the pointer
        }

        End();
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _scroll))
        {
            Finish(cancelled: false); // Alt+Tab, another window, the view stopped hit testing
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The overlay list inside a card scrolls too (the event bubbles): only the view's own scroll.
        if (_started && ReferenceEquals(e.OriginalSource, _scroll))
        {
            Update(Mouse.GetPosition(_surface));
        }
    }

    private void Update(Point pointer)
    {
        if (_done)
        {
            return;
        }

        if (!_started)
        {
            if (!FileDrag.IsDragGesture(
                    pointer.X - _press.X, pointer.Y - _press.Y,
                    SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
            {
                return;
            }

            _started = true;
            AdornerLayer.GetAdornerLayer(_surface)?.Add(_adorner);
            _handlers.Started();
            if (_done)
            {
                return;
            }
        }

        var layout = Measure();
        Point origin = layout?.Origin ?? Origin();
        var rect = MarqueeRect.Between(_anchor.X, _anchor.Y, pointer.X - origin.X, pointer.Y - origin.Y);
        var span = layout is { } l ? MarqueeGrid.Span(rect, l.Grid) : CellSpan.Empty;
        if (span != _span)
        {
            _span = span;
            var box = new List<CardViewModel>();
            if (layout is { } g)
            {
                foreach (int index in MarqueeGrid.Indices(span, g.Grid.Columns, Math.Min(g.Grid.Count, _items.Count)))
                {
                    box.Add(_items[index]);
                }
            }

            _handlers.BoxChanged(box);
            if (_done)
            {
                return;
            }
        }

        _adorner.Show(
            new Rect(new Point(rect.Left + origin.X, rect.Top + origin.Y), new Point(rect.Right + origin.X, rect.Bottom + origin.Y)),
            ViewportRect());
        UpdateTimer();
    }

    /// <summary>The layout now (it moves with the scroll position); remembers its origin for when no row is realized.</summary>
    private MarqueeLayout? Measure()
    {
        if (_measure() is { } layout)
        {
            _lastOrigin = layout.Origin;
            _lastOffset = _scroll.VerticalOffset;
            return layout;
        }

        return null;
    }

    private Point Origin()
    {
        if (Measure() is { } layout)
        {
            return layout.Origin;
        }

        return _lastOrigin is { } last
            ? new Point(last.X, last.Y - (_scroll.VerticalOffset - _lastOffset))
            : new Point(0, -_scroll.VerticalOffset);
    }

    /// <summary>The viewport in surface coordinates: the box is not drawn over the scroll bar or the margins outside it.</summary>
    private Rect ViewportRect()
    {
        try
        {
            return _viewport.TransformToVisual(_surface).TransformBounds(new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty; // not connected any more
        }
    }

    private double EdgeStep() =>
        _scroll.ScrollableHeight > 0
            ? EdgeScroll.OutsideStep(Mouse.GetPosition(_viewport).Y, _viewport.ActualHeight, EdgeInset, EdgeRamp, EdgeMaxStep)
            : 0;

    private void UpdateTimer()
    {
        if (EdgeStep() == 0)
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void AutoScrollTick()
    {
        double step = EdgeStep();
        if (_done || step == 0)
        {
            _timer.Stop();
            return;
        }

        double next = Math.Clamp(_scroll.VerticalOffset + step, 0, _scroll.ScrollableHeight);
        if (next == _scroll.VerticalOffset)
        {
            _timer.Stop(); // at the end of the list
            return;
        }

        _scroll.ScrollToVerticalOffset(next); // ScrollChanged updates the box after the layout
    }

    private void Finish(bool cancelled)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _timer.Stop();
        _scroll.MouseMove -= OnMouseMove;
        _scroll.PreviewMouseLeftButtonUp -= OnLeftUp;
        _scroll.LostMouseCapture -= OnLostCapture;
        _scroll.ScrollChanged -= OnScrollChanged;
        AdornerLayer.GetAdornerLayer(_surface)?.Remove(_adorner);
        if (ReferenceEquals(Mouse.Captured, _scroll))
        {
            _scroll.ReleaseMouseCapture(); // raises LostMouseCapture now: ignored, _done is set
        }

        Finished?.Invoke();
        _handlers.Ended(cancelled, _started);
    }

    private static T? FindDescendant<T>(DependencyObject node)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found)
            {
                return found;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>The box: a 1 px accent frame over a light tint of the accent, clipped to the viewport.</summary>
    private sealed class MarqueeAdorner : Adorner
    {
        private readonly Pen _pen;
        private readonly Brush _fill;
        private Rect _box = Rect.Empty;
        private Rect _clip = Rect.Empty;

        public MarqueeAdorner(UIElement adorned, Color accent)
            : base(adorned)
        {
            IsHitTestVisible = false;
            IsClipEnabled = true;
            var stroke = new SolidColorBrush(accent);
            stroke.Freeze();
            _pen = new Pen(stroke, 1);
            _pen.Freeze();
            var fill = new SolidColorBrush(accent) { Opacity = 0.18 };
            fill.Freeze();
            _fill = fill;
        }

        public void Show(Rect box, Rect clip)
        {
            if (box == _box && clip == _clip)
            {
                return;
            }

            _box = box;
            _clip = clip;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var shown = Rect.Intersect(_box, _clip);
            if (shown.IsEmpty || shown.Width <= 0 || shown.Height <= 0)
            {
                return;
            }

            // Snap the 1 px frame to the pixel grid.
            var guidelines = new GuidelineSet();
            guidelines.GuidelinesX.Add(shown.Left + 0.5);
            guidelines.GuidelinesX.Add(shown.Right + 0.5);
            guidelines.GuidelinesY.Add(shown.Top + 0.5);
            guidelines.GuidelinesY.Add(shown.Bottom + 0.5);
            dc.PushGuidelineSet(guidelines);
            dc.DrawRectangle(_fill, _pen, shown);
            dc.Pop();
        }
    }
}
