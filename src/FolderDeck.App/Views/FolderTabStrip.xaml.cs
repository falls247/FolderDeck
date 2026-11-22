using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FolderDeck.Core.Transfer;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The tab strip (every open tab: 最近の履歴, よく使う項目 and the folder tabs): a press shows the
/// tab, dragging it sideways moves it among the others (they swap as the pointer passes the middle
/// of a neighbour), a middle click or × closes it, the wheel scrolls the strip when the tabs do
/// not fit.
/// </summary>
public partial class FolderTabStrip : UserControl
{
    private MainViewModel? _vm;
    private TabViewModel? _pressed;
    private Point _pressedAt;
    private bool _moving;

    public FolderTabStrip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Tabs.PreviewMouseLeftButtonDown += OnLeftDown;
        Tabs.MouseMove += OnMouseMove;
        Tabs.PreviewMouseLeftButtonUp += (_, _) => EndPress();
        Tabs.LostMouseCapture += (_, _) =>
        {
            _moving = false;
            _pressed = null;
        };
        Tabs.PreviewMouseDown += OnMouseDown;
        Tabs.MouseRightButtonUp += OnRightUp;
        Scroller.PreviewMouseWheel += (_, e) =>
        {
            if (Scroller.ScrollableWidth > 0)
            {
                Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        };
    }

    /// <summary>A tab is about to be shown, moved or closed (the main window closes the cascade).</summary>
    public event Action? Interacted;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }

        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged += OnTabsChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            RevealSelected();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RevealSelected();

    /// <summary>Scrolls the strip so the shown tab is in view (after layout: a new tab has no element yet).</summary>
    private void RevealSelected()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_vm?.SelectedTab is { IsPlaceholder: false } tab && !_moving
                && Tabs.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        });
    }

    private static TabViewModel? TabAt(object? source) =>
        CardView.FindAncestor<ButtonBase>(source as DependencyObject) is null
            ? (source as FrameworkElement)?.DataContext as TabViewModel
            : null; // the × button handles its own clicks

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (TabAt(e.OriginalSource) is not { } tab || _vm is null)
        {
            return;
        }

        e.Handled = true;
        _pressed = tab;
        _pressedAt = e.GetPosition(Tabs);
        _moving = false;
        Interacted?.Invoke();
        _vm.SelectTabCommand.Execute(tab);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } tab || _vm is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point p = e.GetPosition(Tabs);
        if (!_moving)
        {
            if (!FileDrag.IsDragGesture(p.X - _pressedAt.X, 0, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)
                || !Tabs.CaptureMouse())
            {
                return;
            }

            _moving = true; // after CaptureMouse: taking the capture resets the press state of a previous one
            _pressed = tab;
        }

        MoveTowards(tab, p.X);
    }

    /// <summary>
    /// Swaps the dragged tab with a neighbour once the pointer is past that neighbour's middle
    /// (so tabs of different widths do not flip back and forth).
    /// </summary>
    private void MoveTowards(TabViewModel tab, double x)
    {
        if (_vm is null)
        {
            return;
        }

        var tabs = _vm.Tabs;
        int from = tabs.IndexOf(tab);
        if (from < 0)
        {
            return;
        }

        int to = from;
        while (to + 1 < tabs.Count && MiddleOf(tabs[to + 1]) is { } right && x > right)
        {
            to++;
        }

        if (to == from)
        {
            while (to - 1 >= 0 && MiddleOf(tabs[to - 1]) is { } left && x < left)
            {
                to--;
            }
        }

        if (to != from)
        {
            _vm.MoveTab(tab, to);
            Tabs.UpdateLayout(); // the next move is judged against the new positions
        }
    }

    /// <summary>Horizontal middle of a tab, in the strip's coordinates (null: not realized).</summary>
    private double? MiddleOf(TabViewModel tab) =>
        Tabs.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container
            ? container.TranslatePoint(new Point(container.ActualWidth / 2, 0), Tabs).X
            : null;

    private void EndPress()
    {
        bool moving = _moving;
        _moving = false;
        _pressed = null;
        if (moving)
        {
            Tabs.ReleaseMouseCapture();
            RevealSelected();
        }
    }

    /// <summary>Middle click closes the tab under the pointer.</summary>
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TabAt(e.OriginalSource) is { } tab && _vm is not null)
        {
            e.Handled = true;
            Interacted?.Invoke();
            _vm.CloseTabCommand.Execute(tab);
        }
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (TabAt(e.OriginalSource) is not { } tab || _vm is not { } vm || e.OriginalSource is not UIElement target)
        {
            return;
        }

        e.Handled = true;
        Interacted?.Invoke();
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        var close = new MenuItem { Header = "タブを閉じる" };
        close.Click += (_, _) => vm.CloseTabCommand.Execute(tab);
        menu.Items.Add(close);
        var others = new MenuItem { Header = "ほかのタブを閉じる", IsEnabled = vm.Tabs.Count > 1 };
        others.Click += (_, _) => _ = vm.CloseOtherTabsAsync(tab);
        menu.Items.Add(others);
        menu.IsOpen = true;
    }
}
