using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderDeck.Core.Layout;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The view of one pane (DataContext = <see cref="PaneViewModel"/>): its card grid and details list
/// (<see cref="ActiveList"/> is the one its view mode shows), its notice and error banner. Built and
/// laid out by <c>MainWindow</c> from the shown tab's <see cref="PaneLayout{T}"/>; a view is kept for
/// the window's life and given to another pane when its own leaves the screen (a single-pane tab
/// always reuses the same one, as the one grid of the window did before panes).
/// </summary>
public partial class PaneView : UserControl
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(PaneView), new PropertyMetadata(false));

    public static readonly DependencyProperty IsSplitProperty =
        DependencyProperty.Register(nameof(IsSplit), typeof(bool), typeof(PaneView), new PropertyMetadata(false));

    public static readonly DependencyProperty SlotProperty =
        DependencyProperty.Register(nameof(Slot), typeof(int), typeof(PaneView), new PropertyMetadata(-1));

    public static readonly DependencyProperty ShowsEmptyHintProperty =
        DependencyProperty.Register(nameof(ShowsEmptyHint), typeof(bool), typeof(PaneView), new PropertyMetadata(false));

    public static readonly DependencyProperty IsTabDropTargetProperty =
        DependencyProperty.Register(nameof(IsTabDropTarget), typeof(bool), typeof(PaneView), new PropertyMetadata(false));

    public PaneView()
    {
        InitializeComponent();

        // Tunnels before every handler below the pane (cards, rows, the blank area, the column titles):
        // they all act on the active pane, which is this one by then.
        PreviewMouseDown += (_, _) => ActivationRequested?.Invoke(this);
    }

    /// <summary>A press anywhere in the pane: it is to become the active pane.</summary>
    public event Action<PaneView>? ActivationRequested;

    /// <summary>The pane shown, or null while the view is not used.</summary>
    public PaneViewModel? Pane => DataContext as PaneViewModel;

    /// <summary>The active pane of a split tab: drawn with an accent frame.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The window is split (more than one pane on screen): the frame of the active pane is drawn.</summary>
    public bool IsSplit
    {
        get => (bool)GetValue(IsSplitProperty);
        set => SetValue(IsSplitProperty, value);
    }

    /// <summary>The pane's colour while the window is split (its tab's <c>PaneSlot</c>, 0–3); −1 for none.</summary>
    public int Slot
    {
        get => (int)GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <summary>The pane is an empty pane (of a group, or the blank page of ＋): its hint and buttons show.</summary>
    public bool ShowsEmptyHint
    {
        get => (bool)GetValue(ShowsEmptyHintProperty);
        set => SetValue(ShowsEmptyHintProperty, value);
    }

    /// <summary>A tab dragged from the strip would go in this pane if dropped now: the pane is marked.</summary>
    public bool IsTabDropTarget
    {
        get => (bool)GetValue(IsTabDropTargetProperty);
        set => SetValue(IsTabDropTargetProperty, value);
    }

    public CardGrid CardGrid => Grid;

    public DetailsListView DetailsList => Details;

    /// <summary>The items area (under the banner): its blank part is the pane's folder for a drop, a paste, the blank menu.</summary>
    public FrameworkElement ItemsHost => Host;

    /// <summary>The view that has the items: the card grid, or the details list in the details view mode.</summary>
    public IItemsView ActiveList => Pane is { } pane && !CardShapes.IsCards(pane.CardShape) ? Details : Grid;

    /// <summary>Scrolls the shown view; a view not laid out yet (just built or moved) is scrolled once it is.</summary>
    public void ScrollWhenReady(double offset)
    {
        if (IsLoaded && IsArrangeValid)
        {
            ActiveList.ScrollTo(offset);
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Pane is not null)
            {
                ActiveList.ScrollTo(offset);
            }
        });
    }
}
