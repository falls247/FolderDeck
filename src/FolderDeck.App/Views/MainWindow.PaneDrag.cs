using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Paths;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

// ---- drags onto the panes: a tab from the strip (onto a pane, or onto a layout of the snap bar) and a
// folder onto an empty pane ------------------------------------------------------------------------------

public partial class MainWindow
{
    /// <summary>The pane view marked as where the drag would go (null: none).</summary>
    private PaneView? _tabDropView;

    /// <summary>The zones of the snap bar's layouts (<see cref="PanePresets"/>), built when it is first shown.</summary>
    private readonly List<(int Preset, int Zone, Border View)> _snapZones = [];

    /// <summary>The zone of the snap bar under the dragged tab (null: none).</summary>
    private (int Preset, int Zone)? _snapTarget;

    /// <summary>How far down into the panes a dragged tab brings up the snap bar (DIP; above the panes too: the toolbar).</summary>
    private const double SnapTriggerHeight = 40;

    /// <summary>How far outside the snap bar the pointer may go before it closes (DIP).</summary>
    private const double SnapKeepMargin = 24;

    private const double SnapPictureWidth = 72;
    private const double SnapPictureHeight = 46;
    private const double SnapZoneGap = 3;

    /// <summary>
    /// The tab strip's drag is over the panes (<paramref name="screen"/>; null: it left them). At the top
    /// of the panes the snap bar comes down (as Windows 11's snap layouts) and the zone under the pointer
    /// is marked; elsewhere the pane of the group on screen under the pointer is marked, unless it is the
    /// dragged tab's own. True when there is something to drop on.
    /// </summary>
    private bool OnTabDraggedOverPanes(TabLayoutViewModel tab, Point? screen)
    {
        if (screen is { } at && SnapWanted(at))
        {
            MarkDropView(null);
            ShowSnapBar(true);
            SetSnapTarget(SnapZoneAt(at));
            return _snapTarget is not null;
        }

        ShowSnapBar(false);
        var view = screen is { } point && (_vm.IsSplit || _vm.IsBlankPane) ? PaneViewAtScreen(point) : null;
        if (view?.Pane is not { } pane || ReferenceEquals(pane, tab.ActivePane))
        {
            view = null;
        }

        MarkDropView(view);
        return view is not null;
    }

    /// <summary>
    /// The dragged tab was released over the panes: a zone of the snap bar lays the panes out with the
    /// tab there (MainViewModel.PlaceInPresetAsync); a pane under the pointer takes it (PutInPaneAsync).
    /// </summary>
    private bool OnTabDroppedOnPanes(TabLayoutViewModel tab, Point screen)
    {
        bool target = OnTabDraggedOverPanes(tab, screen);
        var pane = _tabDropView?.Pane;
        var snap = _snapTarget;
        OnTabDraggedOverPanes(tab, null);
        if (!target)
        {
            return false;
        }

        _cascade.CloseAll();
        if (snap is { } zone)
        {
            _ = _vm.PlaceInPresetAsync(tab, zone.Preset, zone.Zone);
            return true;
        }

        if (pane is null)
        {
            return false;
        }

        _ = _vm.PutInPaneAsync(tab, pane);
        return true;
    }

    private void MarkDropView(PaneView? view)
    {
        if (ReferenceEquals(view, _tabDropView))
        {
            return;
        }

        if (_tabDropView is not null)
        {
            _tabDropView.IsTabDropTarget = false;
        }

        _tabDropView = view;
        if (view is not null)
        {
            view.IsTabDropTarget = true;
        }
    }

    /// <summary>The pane view under a point of the screen (not a splitter), or null.</summary>
    private PaneView? PaneViewAtScreen(Point screen)
    {
        if (!PaneArea.IsVisible || PresentationSource.FromVisual(PaneArea) is null)
        {
            return null;
        }

        Point p = PaneArea.PointFromScreen(screen);
        if (p.X < 0 || p.Y < 0 || p.X > PaneArea.ActualWidth || p.Y > PaneArea.ActualHeight)
        {
            return null;
        }

        return PaneArea.InputHitTest(p) is DependencyObject hit ? PaneViewOf(hit) : null;
    }

    // ---- the snap bar ----

    /// <summary>The dragged tab is at the top of the panes (or above them, below the strip), or still around the snap bar.</summary>
    private bool SnapWanted(Point screen)
    {
        if (!PaneArea.IsVisible || PresentationSource.FromVisual(PaneArea) is null)
        {
            return false;
        }

        if (SnapBar.Visibility == Visibility.Visible && PresentationSource.FromVisual(SnapBar) is not null)
        {
            Point b = SnapBar.PointFromScreen(screen);
            if (b.X >= -SnapKeepMargin && b.X <= SnapBar.ActualWidth + SnapKeepMargin
                && b.Y <= SnapBar.ActualHeight + SnapKeepMargin)
            {
                return true;
            }
        }

        Point p = PaneArea.PointFromScreen(screen);
        return p.X >= 0 && p.X <= PaneArea.ActualWidth && p.Y < SnapTriggerHeight;
    }

    private void ShowSnapBar(bool show)
    {
        if (show && _snapZones.Count == 0)
        {
            BuildSnapBar();
        }

        if (!show)
        {
            SetSnapTarget(null);
        }

        SnapBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One small picture per layout of <see cref="PanePresets"/>, its zones as boxes.</summary>
    private void BuildSnapBar()
    {
        for (int preset = 0; preset < PanePresets.Count; preset++)
        {
            var picture = new Canvas
            {
                Width = SnapPictureWidth,
                Height = SnapPictureHeight,
                Margin = new Thickness(preset == 0 ? 0 : 10, 0, 0, 0),
            };
            var zones = PanePresets.ZonesOf(preset);
            for (int zone = 0; zone < zones.Count; zone++)
            {
                var (x, y, width, height) = zones[zone];
                var box = new Border
                {
                    Width = Math.Max(1, width * SnapPictureWidth - SnapZoneGap),
                    Height = Math.Max(1, height * SnapPictureHeight - SnapZoneGap),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                };
                box.SetResourceReference(Border.BackgroundProperty, "Brush.Surface3");
                box.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
                Canvas.SetLeft(box, x * SnapPictureWidth + SnapZoneGap / 2);
                Canvas.SetTop(box, y * SnapPictureHeight + SnapZoneGap / 2);
                picture.Children.Add(box);
                _snapZones.Add((preset, zone, box));
            }

            SnapLayouts.Children.Add(picture);
        }
    }

    /// <summary>The zone under a point of the screen (the gaps between zones count as theirs), or null.</summary>
    private (int Preset, int Zone)? SnapZoneAt(Point screen)
    {
        foreach (var (preset, zone, box) in _snapZones)
        {
            if (PresentationSource.FromVisual(box) is null)
            {
                return null;
            }

            Point p = box.PointFromScreen(screen);
            double slack = SnapZoneGap / 2 + 0.5;
            if (p.X >= -slack && p.Y >= -slack && p.X <= box.ActualWidth + slack && p.Y <= box.ActualHeight + slack)
            {
                return (preset, zone);
            }
        }

        return null;
    }

    private void SetSnapTarget((int Preset, int Zone)? target)
    {
        if (target == _snapTarget)
        {
            return;
        }

        foreach (var (preset, zone, box) in _snapZones)
        {
            bool on = target is { } t && t.Preset == preset && t.Zone == zone;
            box.SetResourceReference(Border.BackgroundProperty, on ? "Brush.Accent" : "Brush.Surface3");
        }

        _snapTarget = target;
    }

    // ---- a folder dropped on an empty pane ----

    /// <summary>The folder found in the drag's items last (a drag lists the same items on every DragOver).</summary>
    private (IReadOnlyList<string> Sources, string? Folder)? _emptyPaneFolder;

    /// <summary>
    /// A drag over an empty pane that carries a folder (a folder card or row of another pane, a 登録フォルダ,
    /// a folder from Explorer): the pane, the folder (the first of the items), and the effect to answer
    /// (Link, else Copy: never Move, which would let another app delete what it dragged). Null otherwise.
    /// </summary>
    private (PaneView View, string Folder, DragDropEffects Effect)? EmptyPaneDropOf(DragEventArgs e)
    {
        if (PaneViewOf(e.OriginalSource as DependencyObject) is not { Pane: { } pane } view || !_vm.IsEmptyPane(pane))
        {
            return null;
        }

        var effect = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
            : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
            : DragDropEffects.None;
        IReadOnlyList<string>? sources = FileDragOut.Current is { } own ? own.Paths : ExternalPaths(e.Data);
        return effect != DragDropEffects.None && FirstFolder(sources) is { } folder ? (view, folder, effect) : null;
    }

    private string? FirstFolder(IReadOnlyList<string>? sources)
    {
        if (sources is not { Count: > 0 })
        {
            return null;
        }

        if (_emptyPaneFolder is { } known && ReferenceEquals(known.Sources, sources))
        {
            return known.Folder;
        }

        string? folder = sources.FirstOrDefault(IsFolder);
        _emptyPaneFolder = (sources, folder);
        return folder;

        static bool IsFolder(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>DragOver on an empty pane with a folder: marks the pane. False when the drag is something else.</summary>
    private bool DragOverEmptyPane(DragEventArgs e)
    {
        if (EmptyPaneDropOf(e) is not { } open)
        {
            MarkDropView(null);
            return false;
        }

        SetDropMark(null);
        MarkDropView(open.View);
        e.Effects = open.Effect;
        ShowDropHint($"「{PathTools.DisplayName(open.Folder)}」をこのペインで開く");
        e.Handled = true;
        return true;
    }

    /// <summary>Drop on an empty pane with a folder: it opens there once the drag is over. False when the drop is something else.</summary>
    private bool DropOnEmptyPane(DragEventArgs e)
    {
        var open = EmptyPaneDropOf(e);
        MarkDropView(null);
        _emptyPaneFolder = null;
        if (open is not { View.Pane: { } pane } found)
        {
            return false;
        }

        HideDropHint();
        _externalDrop = null;
        e.Effects = found.Effect;
        e.Handled = true;
        if (FileDragOut.Current is { } own)
        {
            own.OpenedInPane = true; // nothing to move or watch afterwards
        }

        string folder = found.Folder;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = _vm.OpenInEmptyPaneAsync(pane, folder));
        return true;
    }
}
