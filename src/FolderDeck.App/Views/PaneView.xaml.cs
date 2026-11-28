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

    /// <summary>The tab has more than one pane (the frame and the banner's 「ペインを閉じる」).</summary>
    public bool IsSplit
    {
        get => (bool)GetValue(IsSplitProperty);
        set => SetValue(IsSplitProperty, value);
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
