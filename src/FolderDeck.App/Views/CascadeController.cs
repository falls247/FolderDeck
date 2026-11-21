using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FolderDeck.Core.Listing;
using FolderDeck.Services;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// Hover list and cascade (§9): the overlay drawn over a card (level 0) and any number of flyout
/// Popups (levels 1…). Owns hover intent (§9.6), placement (§9.5), clicks (§9.7) and keyboard
/// navigation (§9.9). Every close is decided here; Popups use StaysOpen=True.
/// </summary>
public sealed class CascadeController
{
    private const double CardGap = 8;
    private const double FlyoutGap = 4;
    private const double OverlapShift = 24;
    private const double RowLead = 10;
    private const double WorkMargin = 16;

    private readonly Window _window;
    private readonly MainViewModel _vm;
    private readonly AppServices _services;
    private readonly ContextMenuFactory _menus;
    private readonly List<Level> _levels = [];
    private readonly DispatcherTimer _overlayTimer;
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _graceTimer;
    private readonly DispatcherTimer _exitTimer;

    private CardView? _card;
    private CardViewModel? _waitingFor;
    private PendingOpen? _pendingOpen;
    private int _graceLevel = -1;
    private int _keyLevel = -1;
    private bool _menuOpen;
    private int _generation;

    public CascadeController(Window window, MainViewModel vm, ContextMenuFactory menus)
    {
        _window = window;
        _vm = vm;
        _services = vm.Services;
        _menus = menus;
        _overlayTimer = NewTimer(HoverTimings.Overlay, OnOverlayTimer);
        _openTimer = NewTimer(HoverTimings.Open, OnOpenTimer);
        _graceTimer = NewTimer(HoverTimings.Grace, OnGraceTimer);
        _exitTimer = NewTimer(HoverTimings.Exit, OnExitTimer);
        _menus.MenuOpened += () => _menuOpen = true;
        _menus.MenuClosed += () =>
        {
            _menuOpen = false;
            Restart(_exitTimer); // back to rule 3
        };
        _menus.BeforeNavigate = CloseAll;
    }

    public bool IsOpen => _levels.Count > 0;

    // ---- card events --------------------------------------------------------------------------

    public void CardMouseEnter(CardView card)
    {
        _exitTimer.Stop();
        if (!_window.IsActive)
        {
            return; // rule 7: nothing opens over an inactive window
        }

        if (!ReferenceEquals(card, _card))
        {
            CloseAll(); // rule 4
            _card = card;
        }

        var vm = card.ViewModel;
        if (_levels.Count == 0 && vm is not null && vm.IsFolderLike && !vm.IsDisabled)
        {
            Restart(_overlayTimer);
        }
    }

    public void CardMouseLeave(CardView card)
    {
        if (ReferenceEquals(card, _card))
        {
            Restart(_exitTimer);
        }
    }

    // ---- closing ------------------------------------------------------------------------------

    /// <summary>Closes everything at once (rule 5: scroll, resize, tab switch, navigation, deactivation…).</summary>
    public void CloseAll()
    {
        _overlayTimer.Stop();
        _openTimer.Stop();
        _graceTimer.Stop();
        _exitTimer.Stop();
        _pendingOpen = null;
        _graceLevel = -1;
        _keyLevel = -1;
        StopWaiting();
        CloseFrom(0);
        if (_card?.ViewModel is { } vm)
        {
            vm.IsHoverHeld = false;
        }

        _card = null;
        _generation++;
    }

    /// <summary>Esc (rule 6): closes only the deepest level.</summary>
    public bool CloseDeepest()
    {
        if (_levels.Count == 0)
        {
            return false;
        }

        CloseFrom(_levels.Count - 1);
        return true;
    }

    private void CloseFrom(int index)
    {
        for (int i = _levels.Count - 1; i >= index && i >= 0; i--)
        {
            var level = _levels[i];
            _levels.RemoveAt(i);
            level.Dispose();
            if (level.ParentRow is not null)
            {
                level.ParentRow.IsActive = false;
            }

            if (level.KeyRow is not null)
            {
                level.KeyRow.IsKeySelected = false;
            }

            if (level.Overlay is { } overlay && level.Card is { } card)
            {
                FadeOutOverlay(card, overlay);
                if (card.ViewModel is { } cvm)
                {
                    cvm.IsHoverHeld = false;
                }
            }
        }

        if (index > 0 && index - 1 < _levels.Count)
        {
            _levels[index - 1].ActiveRow = null;
        }

        if (_pendingOpen is { } p && p.Level >= _levels.Count)
        {
            _openTimer.Stop();
            _pendingOpen = null;
        }

        if (_graceLevel >= _levels.Count)
        {
            _graceTimer.Stop();
            _graceLevel = -1;
        }

        if (_keyLevel >= _levels.Count)
        {
            _keyLevel = _levels.Count - 1;
        }
    }

    // ---- overlay (level 0, §9.2) --------------------------------------------------------------

    private void OnOverlayTimer()
    {
        _overlayTimer.Stop();
        var card = _card;
        var vm = card?.ViewModel;
        if (card is null || vm is null || _levels.Count > 0 || !_window.IsActive || !card.IsMouseOver)
        {
            return;
        }

        if (!vm.CountsLoaded)
        {
            // §7.4.1: open once the counts arrive, if the pointer is still there.
            StopWaiting();
            _waitingFor = vm;
            vm.DetailsLoaded += OnDetailsLoaded;
            return;
        }

        _ = OpenOverlayAsync(card, vm);
    }

    private void OnDetailsLoaded(CardViewModel vm)
    {
        if (!ReferenceEquals(vm, _waitingFor))
        {
            return;
        }

        StopWaiting();
        var card = _card;
        if (card is not null && ReferenceEquals(card.ViewModel, vm) && card.IsMouseOver && _window.IsActive && _levels.Count == 0)
        {
            _ = OpenOverlayAsync(card, vm);
        }
    }

    private void StopWaiting()
    {
        if (_waitingFor is not null)
        {
            _waitingFor.DetailsLoaded -= OnDetailsLoaded;
            _waitingFor = null;
        }
    }

    private async Task OpenOverlayAsync(CardView card, CardViewModel vm)
    {
        if (vm.Target is null || vm.IsDisabled || (vm.FolderCount + vm.FileCount) == 0)
        {
            return; // §9.2: only folder cards with at least one child
        }

        int generation = _generation;
        FolderListing? listing;
        try
        {
            listing = vm.Listing ?? await vm.EnsureListingAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            return;
        }

        if (generation != _generation || !ReferenceEquals(_card, card) || !ReferenceEquals(card.ViewModel, vm) || _levels.Count > 0)
        {
            return;
        }

        if (listing is null || !listing.IsOk || listing.Entries.Count == 0)
        {
            return;
        }

        var model = new EntryListViewModel(vm.Path, vm.Target, _services.Icons);
        model.Apply(listing);
        var overlay = new OverlayView { DataContext = model };
        var level = new Level(0, model, overlay.List) { Overlay = overlay, Card = card };
        Wire(level);
        _levels.Add(level);
        card.SetOverlay(overlay);
        vm.IsHoverHeld = true;
        overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, HoverTimings.OverlayFadeIn));
    }

    private static void FadeOutOverlay(CardView card, OverlayView overlay)
    {
        var fade = new DoubleAnimation(overlay.Opacity, 0, HoverTimings.OverlayFadeOut);
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(card.CurrentOverlay, overlay))
            {
                card.SetOverlay(null);
            }
        };
        overlay.IsHitTestVisible = false;
        overlay.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---- rows and hover intent (§9.6) ---------------------------------------------------------

    private void Wire(Level level)
    {
        level.List.RowHovered += (_, row, _) => OnRowHovered(level, row);
        level.List.RowClicked += (_, row) => OnRowClicked(row);
        level.List.RowContextMenu += (_, row, element) => _menus.Show(row.ToItemRef(), element);
        if (level.Flyout is { } flyout)
        {
            flyout.MouseEnter += (_, _) => OnFlyoutEnter(level);
            flyout.MouseLeave += (_, _) => Restart(_exitTimer);
            flyout.HeaderClicked += _ => OnHeaderClicked(level);
            flyout.HeaderContextMenu += _ => _menus.Show(new ItemRef(level.Model.Path, true, level.Model.Location, ItemContext.FlyoutHeader), flyout.HeaderElement);
            flyout.CardElement.SizeChanged += (_, _) => Position(level, reveal: false);
        }
    }

    private void OnRowHovered(Level level, EntryRowViewModel? row)
    {
        if (!_levels.Contains(level))
        {
            return;
        }

        _exitTimer.Stop();
        if (row is not null && row.IsFolder)
        {
            // Rule 1 / 2: resting T_open on a folder row (re)opens its child.
            StopGrace();
            _pendingOpen = new PendingOpen(level.Index, row);
            Restart(_openTimer);
            return;
        }

        // File row or blank space.
        if (_pendingOpen is { } p && p.Level == level.Index)
        {
            _openTimer.Stop();
            _pendingOpen = null;
        }

        if (level.ActiveRow is not null && !(_graceTimer.IsEnabled && _graceLevel == level.Index))
        {
            _graceLevel = level.Index;
            Restart(_graceTimer);
        }
    }

    private void OnFlyoutEnter(Level level)
    {
        _exitTimer.Stop();
        if (_graceTimer.IsEnabled && _graceLevel < level.Index)
        {
            StopGrace(); // reached the child: keep it open
        }

        if (_pendingOpen is { } p && p.Level < level.Index)
        {
            _openTimer.Stop();
            _pendingOpen = null;
        }
    }

    private void OnOpenTimer()
    {
        _openTimer.Stop();
        if (_pendingOpen is not { } p)
        {
            return;
        }

        _pendingOpen = null;
        if (!_window.IsActive || p.Level >= _levels.Count)
        {
            return;
        }

        var level = _levels[p.Level];
        if (!ReferenceEquals(level.List.HoveredRow, p.Row))
        {
            return;
        }

        if (ReferenceEquals(level.ActiveRow, p.Row))
        {
            CloseFrom(p.Level + 2); // already open: drop anything deeper than its child
            return;
        }

        OpenChild(level, p.Row);
    }

    private void OnGraceTimer()
    {
        _graceTimer.Stop();
        int index = _graceLevel;
        _graceLevel = -1;
        if (index < 0 || index >= _levels.Count)
        {
            return;
        }

        var level = _levels[index];
        // Still away from the active row (and not inside its child): close the child.
        if (level.ActiveRow is not null && !ReferenceEquals(level.List.HoveredRow, level.ActiveRow) && !IsInsideDeeper(index))
        {
            CloseFrom(index + 1);
        }
    }

    private void OnExitTimer()
    {
        _exitTimer.Stop();
        if (_menuOpen)
        {
            return; // rule 8
        }

        if (IsPointerInside())
        {
            return;
        }

        CloseAll(); // rule 3
    }

    private bool IsPointerInside()
    {
        if (_card?.IsMouseOver == true)
        {
            return true;
        }

        foreach (var level in _levels)
        {
            if (level.Flyout?.IsMouseOver == true)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInsideDeeper(int index)
    {
        for (int i = index + 1; i < _levels.Count; i++)
        {
            if (_levels[i].Flyout?.IsMouseOver == true)
            {
                return true;
            }
        }

        return false;
    }

    private void StopGrace()
    {
        _graceTimer.Stop();
        _graceLevel = -1;
    }

    // ---- clicks (§9.7) ------------------------------------------------------------------------

    private void OnRowClicked(EntryRowViewModel row)
    {
        var item = row.ToItemRef();
        CloseAll();
        _ = _vm.OpenItemAsync(item);
    }

    private void OnHeaderClicked(Level level)
    {
        var item = new ItemRef(level.Model.Path, true, level.Model.Location, ItemContext.FlyoutHeader);
        CloseAll();
        _ = _vm.OpenItemAsync(item);
    }

    // ---- flyouts (levels 1…, §9.3) ------------------------------------------------------------

    private void OpenChild(Level parent, EntryRowViewModel row)
    {
        CloseFrom(parent.Index + 1);
        var container = parent.List.ContainerOf(row);
        if (container is null || !container.IsVisible)
        {
            return;
        }

        Rect rowRect = ScreenRect(container);
        Rect anchor = parent.Flyout is { } pf ? ScreenRect(pf.CardElement) : ScreenRect(parent.Card!);
        var monitor = ShellInterop.GetMonitorArea(new Point(rowRect.Left + (rowRect.Width / 2), rowRect.Top + (rowRect.Height / 2)));

        var model = new EntryListViewModel(row.Path, row.Target, _services.Icons);
        var view = new FlyoutView { DataContext = model };
        view.CardMaxHeight = Math.Max(120, (monitor.Work.Height / monitor.Scale) - WorkMargin);
        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Placement = PlacementMode.Absolute,
            PopupAnimation = PopupAnimation.None,
            Child = view,
        };

        var level = new Level(parent.Index + 1, model, view.List)
        {
            Flyout = view,
            Popup = popup,
            ParentRow = row,
            RowRect = rowRect,
            AnchorRect = anchor,
            PreferLeft = parent.Index > 0 && parent.DirectionLeft, // keep going left once we went left
        };
        _levels.Add(level);
        parent.ActiveRow = row;
        row.IsActive = true;
        Wire(level);

        // Rough position first (main window DPI), exact position once the popup has a size.
        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        double guessX = (level.PreferLeft ? anchor.Left - ((FlyoutView.CardWidth + FlyoutView.ShadowMargin) * scale) : anchor.Right) / scale;
        popup.HorizontalOffset = guessX;
        popup.VerticalOffset = (rowRect.Top / scale) - RowLead - FlyoutView.ShadowMargin;
        popup.IsOpen = true;

        _ = LoadLevelAsync(level);
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Position(level, reveal: true));
    }

    private async Task LoadLevelAsync(Level level)
    {
        var ct = level.Cts.Token;
        try
        {
            var task = _services.Listings.GetAsync(level.Model.Path, ct);
            if (!task.IsCompleted)
            {
                var slow = Task.Delay(HoverTimings.Slow, ct);
                if (await Task.WhenAny(task, slow) == slow && !task.IsCompleted)
                {
                    level.Model.MarkSlow();
                }
            }

            var listing = await task;
            if (!_levels.Contains(level))
            {
                return;
            }

            level.Model.Apply(listing);
            if (level.SelectFirstWhenLoaded && level.Model.Rows.Count > 0)
            {
                level.SelectFirstWhenLoaded = false;
                SetKeyRow(level, level.Model.Rows[0]);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// §9.5 placement, computed in physical pixels on the row's monitor work area, then applied to
    /// the popup window directly (robust across monitors with different DPI).
    /// </summary>
    private void Position(Level level, bool reveal)
    {
        if (!_levels.Contains(level) || level.Popup is null || level.Flyout is null)
        {
            return;
        }

        if (PresentationSource.FromVisual(level.Flyout) is not HwndSource source)
        {
            return;
        }

        var row = level.RowRect;
        var monitor = ShellInterop.GetMonitorArea(new Point(row.Left + (row.Width / 2), row.Top + (row.Height / 2)));
        double s = monitor.Scale;
        var work = monitor.Work;
        double w = FlyoutView.CardWidth * s;
        double h = Math.Max(1, level.Flyout.CardElement.ActualHeight) * s;
        double margin = FlyoutView.ShadowMargin * s;
        double gap = (level.Index == 1 ? CardGap : FlyoutGap) * s;
        var a = level.AnchorRect;

        double rightX = a.Right + gap;
        double leftX = a.Left - gap - w;
        bool rightFits = rightX + w <= work.Right;
        bool leftFits = leftX >= work.Left;
        double x;
        bool left;
        if (level.PreferLeft)
        {
            (x, left) = leftFits ? (leftX, true) : rightFits ? (rightX, false) : (a.Right - (OverlapShift * s) - w, true);
        }
        else
        {
            (x, left) = rightFits ? (rightX, false) : leftFits ? (leftX, true) : (a.Left + (OverlapShift * s), false);
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        double y = row.Top - (RowLead * s);
        if (y + h > work.Bottom)
        {
            y = work.Bottom - h;
        }

        if (y < work.Top)
        {
            y = work.Top;
        }

        int wx = (int)Math.Round(x - margin);
        int wy = (int)Math.Round(y - margin);
        double mainScale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        double offX = wx / mainScale;
        double offY = wy / mainScale;
        if (Math.Abs(level.Popup.HorizontalOffset - offX) > 0.5)
        {
            level.Popup.HorizontalOffset = offX;
        }

        if (Math.Abs(level.Popup.VerticalOffset - offY) > 0.5)
        {
            level.Popup.VerticalOffset = offY;
        }

        ShellInterop.MoveWindow(source.Handle, wx, wy);
        level.TargetX = wx;
        level.TargetY = wy;

        // Which way did it actually go? Judge from the real window rectangle and pass it down.
        if (ShellInterop.GetWindowRect(source.Handle) is { } actual)
        {
            double center = actual.Left + (actual.Width / 2);
            left = center < a.Left + (a.Width / 2);
        }

        level.DirectionLeft = left;

        if (reveal)
        {
            level.Flyout.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, HoverTimings.FlyoutFadeIn));
        }

        // WPF may re-place the popup on its own (DPI change, screen clamping): correct it once more.
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => VerifyPosition(level, source));
    }

    private void VerifyPosition(Level level, HwndSource source)
    {
        if (!_levels.Contains(level) || source.IsDisposed)
        {
            return;
        }

        if (ShellInterop.GetWindowRect(source.Handle) is { } actual
            && (Math.Abs(actual.Left - level.TargetX) > 1 || Math.Abs(actual.Top - level.TargetY) > 1))
        {
            ShellInterop.MoveWindow(source.Handle, level.TargetX, level.TargetY);
        }
    }

    private static Rect ScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    // ---- keyboard (§9.9) ----------------------------------------------------------------------

    /// <summary>Handles ↑ ↓ → ← Enter Esc while the cascade is open.</summary>
    public bool HandleKey(Key key)
    {
        if (_levels.Count == 0)
        {
            return false;
        }

        int index = _keyLevel < 0 || _keyLevel >= _levels.Count ? _levels.Count - 1 : _keyLevel;
        var level = _levels[index];
        switch (key)
        {
            case Key.Escape:
                CloseDeepest();
                return true;
            case Key.Down:
            case Key.Up:
                _keyLevel = index;
                MoveSelection(level, key == Key.Down ? 1 : -1);
                return true;
            case Key.Right:
                _keyLevel = index;
                OpenSelectedChild(level);
                return true;
            case Key.Left:
                if (index > 0)
                {
                    CloseFrom(index);
                    _keyLevel = index - 1;
                }

                return true;
            case Key.Enter:
                if (level.KeyRow is { } row)
                {
                    OnRowClicked(row);
                }

                return true;
        }

        return false;
    }

    private void MoveSelection(Level level, int delta)
    {
        var rows = level.Model.Rows;
        if (rows.Count == 0)
        {
            return;
        }

        int current = level.KeyRow is null ? -1 : IndexOf(rows, level.KeyRow);
        int next = current < 0 ? (delta > 0 ? 0 : rows.Count - 1) : Math.Clamp(current + delta, 0, rows.Count - 1);
        SetKeyRow(level, rows[next]);
    }

    private void SetKeyRow(Level level, EntryRowViewModel row)
    {
        if (level.KeyRow is not null)
        {
            level.KeyRow.IsKeySelected = false;
        }

        level.KeyRow = row;
        row.IsKeySelected = true;
        level.List.ScrollRowIntoView(row);
    }

    private void OpenSelectedChild(Level level)
    {
        if (level.KeyRow is not { IsFolder: true } row)
        {
            return;
        }

        if (!ReferenceEquals(level.ActiveRow, row))
        {
            level.List.ScrollRowIntoView(row);
            level.List.UpdateLayout();
            OpenChild(level, row);
        }

        if (level.Index + 1 >= _levels.Count)
        {
            return;
        }

        var child = _levels[level.Index + 1];
        _keyLevel = child.Index;
        if (child.Model.Rows.Count > 0)
        {
            SetKeyRow(child, child.Model.Rows[0]);
        }
        else
        {
            child.SelectFirstWhenLoaded = true;
        }
    }

    private static int IndexOf(IReadOnlyList<EntryRowViewModel> rows, EntryRowViewModel row)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static DispatcherTimer NewTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private sealed record PendingOpen(int Level, EntryRowViewModel Row);

    private sealed class Level(int index, EntryListViewModel model, EntryListView list) : IDisposable
    {
        public int Index { get; } = index;

        public EntryListViewModel Model { get; } = model;

        public EntryListView List { get; } = list;

        public OverlayView? Overlay { get; init; }

        public CardView? Card { get; init; }

        public FlyoutView? Flyout { get; init; }

        public Popup? Popup { get; init; }

        /// <summary>Row of the parent level that opened this flyout.</summary>
        public EntryRowViewModel? ParentRow { get; init; }

        /// <summary>Row of this level whose child flyout is open.</summary>
        public EntryRowViewModel? ActiveRow { get; set; }

        public EntryRowViewModel? KeyRow { get; set; }

        public bool SelectFirstWhenLoaded { get; set; }

        /// <summary>Screen rectangles (physical pixels) captured when the flyout opened.</summary>
        public Rect RowRect { get; init; }

        public Rect AnchorRect { get; init; }

        public bool PreferLeft { get; init; }

        public bool DirectionLeft { get; set; }

        public int TargetX { get; set; }

        public int TargetY { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public void Dispose()
        {
            Cts.Cancel();
            if (Popup is not null)
            {
                Popup.IsOpen = false;
                Popup.Child = null;
            }
        }
    }
}
