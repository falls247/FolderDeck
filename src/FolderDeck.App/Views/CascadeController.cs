using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Services;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// Hover list and cascade (§9): the overlay drawn over a card (level 0) and any number of flyout
/// Popups (levels 1…). Owns hover intent (§9.6), placement (§9.5), clicks (§9.7) and keyboard
/// navigation (§9.9). Every close is decided here; Popups use StaysOpen=True.
/// <para>
/// Resting on a previewable file row (image, Office file, text, anything the shell draws a
/// thumbnail of), or selecting it with the keyboard, shows a preview beside the list, in the
/// place a child flyout would take. At most one preview is open; it is always the deepest thing
/// in the cascade and closes with the level it belongs to.
/// </para>
/// </summary>
public sealed class CascadeController
{
    private const double CardGap = 8;
    private const double FlyoutGap = 4;
    private const double OverlapShift = 24;
    private const double RowLead = 10;
    private const double WorkMargin = 16;

    /// <summary>How long to wait for the foreground to come back after Explorer's menu (see <see cref="SetNativeMenuOpen"/>).</summary>
    private static readonly TimeSpan NativeMenuHandBack = TimeSpan.FromMilliseconds(300);

    private readonly Window _window;
    private readonly MainViewModel _vm;
    private readonly AppServices _services;
    private readonly ContextMenuFactory _menus;
    private readonly List<Level> _levels = [];
    private readonly DispatcherTimer _overlayTimer;
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _graceTimer;
    private readonly DispatcherTimer _exitTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _previewCloseTimer;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly PreviewTextCache _previewTexts = new();

    private CardView? _card;

    /// <summary>
    /// What the cascade was (or is about to be) opened from when that is not a card (a sidebar row):
    /// the element the pointer rests on, the element the list is placed beside (often the same) and
    /// the folder it stands for. Then level 0 is a flyout beside the element (not an overlay on a
    /// card), and <see cref="ICascadeAnchor.OpensInNewTab"/> says where its folders open. The three
    /// are set and cleared together, and never together with <see cref="_card"/>.
    /// </summary>
    private FrameworkElement? _anchor;
    private FrameworkElement? _anchorPlacement;
    private ICascadeAnchor? _anchorItem;

    /// <summary>
    /// Another anchor the pointer is on while a list is open: the cascade moves there only when the
    /// pointer rests on it (it may just be crossing the element on its way to the list).
    /// </summary>
    private FrameworkElement? _pendingAnchor;
    private FrameworkElement? _pendingAnchorPlacement;
    private ICascadeAnchor? _pendingAnchorItem;
    private CardViewModel? _waitingFor;
    private PendingOpen? _pendingOpen;
    private PendingPreview? _pendingPreview;
    private Preview? _preview;
    private int _graceLevel = -1;
    private int _keyLevel = -1;
    private bool _menuOpen;
    private bool _suspended;
    private bool _dragging;

    /// <summary>Explorer's own menu is open (see <see cref="SetNativeMenuOpen"/>): the cascade is frozen.</summary>
    private bool _nativeMenu;

    /// <summary>
    /// A CloseAll arrived while the native menu was being prepared and the window was still active
    /// (a tab click, a scroll, the end of a drag: not the Deactivated the menu itself causes): run
    /// it as soon as the menu is over.
    /// </summary>
    private bool _closeAfterNativeMenu;
    private int _generation;

    /// <summary>The foreground window when a cascade opened over the inactive main window (see <see cref="WatchForeground"/>).</summary>
    private nint _foregroundAtOpen;

    /// <summary>Where the pointer was first seen on an anchor that has no cascade (see <see cref="AnchorMouseMove"/>).</summary>
    private Point? _rearmFrom;

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
        _previewTimer = NewTimer(HoverTimings.Preview, OnPreviewTimer);
        _previewCloseTimer = NewTimer(HoverTimings.Grace, OnPreviewCloseTimer);
        _foregroundTimer = NewTimer(HoverTimings.ForegroundWatch, OnForegroundTimer);
        _menus.MenuOpened += () => _menuOpen = true;
        _menus.MenuClosed += () =>
        {
            _menuOpen = false;
            if (!_nativeMenu)
            {
                Restart(_exitTimer); // back to rule 3 (after Explorer's menu: see SetNativeMenuOpen)
            }
        };
        _menus.BeforeNavigate = CloseAll;
        _menus.NativeMenuChanged += SetNativeMenuOpen;
    }

    /// <summary>
    /// Something of the cascade is showing. A preview without a level under it only exists beside
    /// an anchor (a file row of the details list, <see cref="ICascadeAnchor.File"/>).
    /// </summary>
    public bool IsOpen => _levels.Count > 0 || _preview is not null;

    /// <summary>
    /// A drag started (a card, a row or a preview): the whole cascade disappears at once and nothing
    /// opens on hover until <see cref="EndDrag"/>, which then really closes it. Taking it away at the
    /// start, rather than leaving it up during the drag, is deliberate:
    /// <list type="bullet">
    /// <item>The flyouts and the preview are topmost popup windows: left visible they would cover the
    /// place the user drops on (the slide in PowerPoint, the folder in Explorer).</item>
    /// <item>The hover timers (overlay, open, grace, exit, preview) keep ticking inside DoDragDrop's
    /// modal loop while the pointer state they check (IsMouseOver, hovered rows) no longer updates,
    /// so they would close or open levels at random under the drag. They are all stopped here.</item>
    /// <item>It matches the reorder drag, which always closed the cascade.</item>
    /// </list>
    /// The popups are only hidden (their content made invisible, so the layered windows are fully
    /// transparent and the pointer and the drop go through them), not destroyed, until the drag ends:
    /// destroying the popup window that received the button press while the button is still down
    /// leaves DoDragDrop unaware of the release (observed: the drag only ended at the next click).
    /// For the same reason <see cref="CloseAll"/> does nothing during a drag (the window losing
    /// activation when the pointer rests on the taskbar, the grid auto-scrolling…).
    /// The drag itself never depends on the cascade's elements: the paths are captured first and the
    /// drag source is the main window (or the card grid), which lives through the whole drag.
    /// </summary>
    public void BeginDrag()
    {
        _dragging = true;
        _suspended = true;
        StopTimers();
        foreach (var level in _levels)
        {
            Hide(level.Overlay);
            Hide(level.Flyout);
        }

        Hide(_preview?.View);

        static void Hide(UIElement? element)
        {
            if (element is not null)
            {
                element.IsHitTestVisible = false;
                element.Visibility = Visibility.Hidden;
            }
        }
    }

    /// <summary>The drag ended (dropped, refused or cancelled): closes the hidden cascade for real.</summary>
    public void EndDrag()
    {
        _dragging = false;
        CloseAll();
        _suspended = false;
    }

    /// <summary>
    /// Explorer's own menu (「その他のメニュー（Windows）」) opens or closes. It runs on another thread
    /// and takes the foreground, so meanwhile the main window is inactive and gets no pointer input.
    /// <para>
    /// <c>true</c> (raised before the menu is even built): the cascade is frozen, not hidden; the
    /// menu was asked for from one of its rows and comes back to it. <see cref="CloseAll"/> does
    /// nothing (Deactivated, a tab that finishes loading…), no hover opens anything, every timer is
    /// stopped and their tick handlers return at once. <c>_suspended</c> is needed as well:
    /// <see cref="CardMouseEnter"/> and <see cref="AnchorMouseEnter"/> would otherwise call
    /// <see cref="CloseAll"/> (which does nothing) and then put a new owner under the open levels.
    /// </para>
    /// <para>
    /// <c>false</c> (every outcome, also a menu that never showed): with the window active again the
    /// cascade goes back to rule 3 (it closes when the pointer is not in it). The menu thread hands
    /// the foreground back only when nothing else took it, and that arrives a moment later, so an
    /// inactive window is judged after <see cref="NativeMenuHandBack"/>: still inactive means the
    /// user went to another window (or a command opened one), and the cascade closes as on
    /// Deactivated.
    /// </para>
    /// </summary>
    public void SetNativeMenuOpen(bool open)
    {
        if (open)
        {
            _nativeMenu = true;
            _suspended = true;
            StopTimers();
            return;
        }

        if (!_nativeMenu)
        {
            return;
        }

        _nativeMenu = false;
        _suspended = _dragging;
        if (_closeAfterNativeMenu)
        {
            _closeAfterNativeMenu = false;
            CloseAll();
            return;
        }

        if (_window.IsActive)
        {
            Restart(_exitTimer);
            return;
        }

        _ = AfterNativeMenuAsync(_generation);
    }

    private async Task AfterNativeMenuAsync(int generation)
    {
        await Task.Delay(NativeMenuHandBack);
        if (_nativeMenu || generation != _generation)
        {
            return; // another menu, or the cascade was closed or replaced meanwhile
        }

        if (_window.IsActive)
        {
            Restart(_exitTimer);
        }
        else
        {
            CloseAll();
        }
    }

    // ---- card events --------------------------------------------------------------------------

    public void CardMouseEnter(CardView card)
    {
        _exitTimer.Stop();
        if (_suspended)
        {
            return; // dragging a card
        }

        // Rule 7 (revised): the window does not have to be active. FolderDeck is used beside other
        // apps, and a hover that silently did nothing until the window was clicked read as a bug.
        // What the old rule protected is kept by RestartOverlay (a longer rest while inactive),
        // WatchForeground (closes when another window comes to the front) and ActivateWindow.
        if (!ReferenceEquals(card, _card))
        {
            CloseAll(); // rule 4
            _card = card;
        }

        var vm = card.ViewModel;
        if (_levels.Count == 0 && vm is not null && vm.IsFolderLike && !vm.IsDisabled)
        {
            RestartOverlay();
        }
    }

    public void CardMouseLeave(CardView card)
    {
        if (ReferenceEquals(card, _card))
        {
            Restart(_exitTimer);
        }
    }

    // ---- anchor events (sidebar rows) ---------------------------------------------------------

    /// <summary>
    /// The pointer entered <paramref name="hover"/>, which stands for the folder
    /// <paramref name="item"/> (a 登録フォルダ or drive row of the sidebar): resting there opens the
    /// folder's list beside <paramref name="placement"/>, at the height of <paramref name="hover"/>
    /// (the same hover intent as a card's overlay). The anchor is known by <paramref name="hover"/>:
    /// the later events of the same anchor must pass the same element.
    /// </summary>
    public void AnchorMouseEnter(FrameworkElement hover, FrameworkElement placement, ICascadeAnchor item)
    {
        _exitTimer.Stop();
        _rearmFrom = null;
        if (_suspended)
        {
            return; // dragging (an inactive window is fine: see CardMouseEnter)
        }

        if (ReferenceEquals(hover, _anchor))
        {
            // Back on the anchor the list belongs to: it stays (a switch that was pending is off).
            if (_pendingAnchor is not null)
            {
                ClearPendingAnchor();
                _overlayTimer.Stop();
            }
        }
        else if (_anchor is not null && IsOpen)
        {
            // Another anchor while a list is open: sidebar rows are stacked, so the pointer crosses
            // the neighbours on its way to the list. Switch only when it rests here.
            _pendingAnchor = hover;
            _pendingAnchorPlacement = placement;
            _pendingAnchorItem = item;
            RestartOverlay();
            return;
        }
        else
        {
            CloseAll(); // rule 4
            _anchor = hover;
            _anchorPlacement = placement;
            _anchorItem = item;
        }

        if (!IsOpen && !item.IsDisabled)
        {
            RestartOverlay();
        }
    }

    /// <summary>
    /// The pointer moved on an anchor (<paramref name="position"/> in coordinates that do not move
    /// with the anchor, such as those of the view it is in).
    /// A hover starts at MouseEnter only, so after everything was closed under a resting pointer
    /// (the window lost activation, a drag ended, another window came to the front…) nothing would
    /// open until the pointer left the anchor and came back. A real move, further than the system
    /// drag distance from where the pointer was first seen, arms the anchor again.
    /// </summary>
    public void AnchorMouseMove(FrameworkElement hover, FrameworkElement placement, ICascadeAnchor item, Point position)
    {
        if (_suspended || IsOpen || _anchor is not null)
        {
            _rearmFrom = null;
            return; // armed or open already
        }

        if (_rearmFrom is not { } from)
        {
            _rearmFrom = position;
            return;
        }

        if (Math.Abs(position.X - from.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(position.Y - from.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            AnchorMouseEnter(hover, placement, item);
        }
    }

    public void AnchorMouseLeave(FrameworkElement hover)
    {
        _rearmFrom = null;
        if (ReferenceEquals(hover, _pendingAnchor))
        {
            ClearPendingAnchor();
            _overlayTimer.Stop();
            Restart(_exitTimer);
        }
        else if (ReferenceEquals(hover, _anchor))
        {
            Restart(_exitTimer);
        }
    }

    /// <summary>
    /// An anchor's element went away while the pointer may still be on it (a drive was unplugged and
    /// its row left the sidebar). The element raises no MouseLeave, so nothing else would close the
    /// list beside it or call off a hover that is waiting to open or to switch to it.
    /// </summary>
    public void AnchorRemoved(ICascadeAnchor item)
    {
        if (ReferenceEquals(item, _pendingAnchorItem))
        {
            ClearPendingAnchor();
            _overlayTimer.Stop();
        }

        if (ReferenceEquals(item, _anchorItem))
        {
            _rearmFrom = null;
            CloseAll();
        }
    }

    private void ClearPendingAnchor()
    {
        _pendingAnchor = null;
        _pendingAnchorPlacement = null;
        _pendingAnchorItem = null;
    }

    // ---- closing ------------------------------------------------------------------------------

    /// <summary>
    /// Closes everything at once (rule 5: scroll, resize, tab switch, navigation, deactivation…).
    /// During a drag the cascade is already hidden and closes when the drag ends (see <see cref="BeginDrag"/>);
    /// while Explorer's menu is open it stays as it is (see <see cref="SetNativeMenuOpen"/>).
    /// </summary>
    public void CloseAll()
    {
        if (_nativeMenu && _window.IsActive)
        {
            _closeAfterNativeMenu = true;
        }

        if (_dragging || _nativeMenu)
        {
            return;
        }

        _closeAfterNativeMenu = false;
        StopTimers();
        _keyLevel = -1;
        CloseFrom(0);
        if (_card?.ViewModel is { } vm)
        {
            vm.IsHoverHeld = false;
        }

        if (_anchorItem is { } item)
        {
            item.IsHoverHeld = false;
        }

        _card = null;
        _anchor = null;
        _anchorPlacement = null;
        _anchorItem = null;
        _generation++;
    }

    /// <summary>
    /// Closes a cascade that hangs on an item of the shown tab (a card), for reasons that only
    /// concern the tab's content (it scrolled, its items were rebuilt). A cascade opened from outside
    /// the tab (a sidebar row: <see cref="ICascadeAnchor.OpensInNewTab"/>) is not anchored to the
    /// content and stays: a tab that finishes loading must not take away the list the user is in.
    /// </summary>
    public void CloseCardCascade()
    {
        if (_anchorItem is not { OpensInNewTab: true })
        {
            CloseAll();
        }
    }

    /// <summary>
    /// Ctrl or Shift went down: the user is about to select items of the list. What hangs on an item
    /// of the list (a card's overlay, the list beside a details row: an anchor that
    /// <see cref="ICascadeAnchor.YieldsToModifiers"/>) closes, since it covers the items next to it.
    /// A list beside a sidebar row is not in the way and stays.
    /// </summary>
    public void CloseForSelectionKeys()
    {
        if (IsOpen && (_card is not null || _anchorItem is { YieldsToModifiers: true }))
        {
            CloseAll();
        }
    }

    /// <summary>Ctrl or Shift is held (read when a hover timer fires).</summary>
    private static bool SelectionKeysDown => (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

    /// <summary>The anchor gives way to a selection gesture that is under way (Ctrl / Shift held).</summary>
    private static bool Yields(ICascadeAnchor item) => item.YieldsToModifiers && SelectionKeysDown;

    /// <summary>Stops every hover timer and forgets what they were about to open or close.</summary>
    private void StopTimers()
    {
        _foregroundTimer.Stop();
        _overlayTimer.Stop();
        _openTimer.Stop();
        _graceTimer.Stop();
        _exitTimer.Stop();
        _previewTimer.Stop();
        _previewCloseTimer.Stop();
        _pendingOpen = null;
        _pendingPreview = null;
        ClearPendingAnchor();
        _graceLevel = -1;
        StopWaiting();
    }

    /// <summary>Esc (rule 6): closes only the deepest level (the preview, when one is open).</summary>
    public bool CloseDeepest()
    {
        if (_preview is not null)
        {
            ClosePreview();
            return true;
        }

        if (_levels.Count == 0)
        {
            return false;
        }

        CloseFrom(_levels.Count - 1);
        return true;
    }

    private void CloseFrom(int index)
    {
        // The preview sits where level ParentIndex + 1 would be.
        if (_preview is { } preview && preview.ParentIndex + 1 >= index)
        {
            ClosePreview();
        }

        if (_pendingPreview is { } pending && pending.Level >= index)
        {
            _previewTimer.Stop();
            _pendingPreview = null;
        }

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

            if (i == 0 && level.Flyout is not null && _anchorItem is { } anchorItem)
            {
                anchorItem.IsHoverHeld = false; // the list beside an anchor closed
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
        if (_nativeMenu)
        {
            return; // frozen under Explorer's menu
        }

        if (_pendingAnchor is { } pending && _pendingAnchorPlacement is { } pendingPlacement && _pendingAnchorItem is { } pendingItem)
        {
            // The pointer rested on another anchor: the cascade moves there.
            ClearPendingAnchor();
            if (_suspended || !pending.IsMouseOver || Yields(pendingItem))
            {
                return;
            }

            CloseAll();
            _anchor = pending;
            _anchorPlacement = pendingPlacement;
            _anchorItem = pendingItem;
            if (!pendingItem.IsDisabled)
            {
                OpenRoot(pending, pendingPlacement, pendingItem);
            }

            return;
        }

        if (_anchor is { } anchor && _anchorPlacement is { } placement && _anchorItem is { } item)
        {
            if (!_suspended && !IsOpen && anchor.IsMouseOver && !item.IsDisabled && !Yields(item))
            {
                OpenRoot(anchor, placement, item);
            }

            return;
        }

        var card = _card;
        var vm = card?.ViewModel;
        if (card is null || vm is null || _suspended || _levels.Count > 0 || !card.IsMouseOver || SelectionKeysDown)
        {
            return; // with Ctrl / Shift the user is selecting cards: an overlay would cover the next one
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
        if (card is not null && !_suspended && ReferenceEquals(card.ViewModel, vm) && card.IsMouseOver && _levels.Count == 0 && !SelectionKeysDown)
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

        if (_suspended || generation != _generation || !ReferenceEquals(_card, card) || !ReferenceEquals(card.ViewModel, vm) || _levels.Count > 0 || SelectionKeysDown)
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
        WatchForeground();
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
        level.List.RowContextMenu += (_, row, element) => ShowMenu(ItemOf(row), element);
        level.List.RowExplorerRequested += (_, row) => OnRowExplorer(row);
        level.List.RowDragRequested += (_, row) => DragOut(row, "row");

        // Anything opened from this list is anchored to a row that just moved.
        level.List.Scrolled += _ =>
        {
            if (_levels.Contains(level))
            {
                CloseFrom(level.Index + 1);
            }
        };
        if (level.Flyout is { } flyout)
        {
            flyout.MouseEnter += (_, _) => OnFlyoutEnter(level);
            flyout.MouseLeave += (_, _) => Restart(_exitTimer);
            flyout.HeaderClicked += _ => OnHeaderClicked(level);
            flyout.HeaderContextMenu += _ => ShowMenu(HeaderOf(level), flyout.HeaderElement);
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
        OnPreviewHover(level, row);
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
        if (_pendingAnchor is not null)
        {
            // Reached the list: the anchor crossed on the way does not take the cascade.
            ClearPendingAnchor();
            _overlayTimer.Stop();
        }

        if (_graceTimer.IsEnabled && _graceLevel < level.Index)
        {
            StopGrace(); // reached the child: keep it open
        }

        if (_pendingOpen is { } p && p.Level < level.Index)
        {
            _openTimer.Stop();
            _pendingOpen = null;
        }

        if (_pendingPreview is { } pp && pp.Level < level.Index)
        {
            _previewTimer.Stop();
            _pendingPreview = null;
        }
    }

    private void OnOpenTimer()
    {
        _openTimer.Stop();
        if (_nativeMenu)
        {
            return; // frozen under Explorer's menu
        }

        if (_pendingOpen is not { } p)
        {
            return;
        }

        _pendingOpen = null;
        if (p.Level >= _levels.Count)
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
        if (_nativeMenu)
        {
            return; // frozen under Explorer's menu
        }

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
        if (_nativeMenu)
        {
            return; // frozen under Explorer's menu
        }

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
        if (_card?.IsMouseOver == true || _anchor?.IsMouseOver == true)
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

        return _preview?.View.IsMouseOver == true;
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
        var item = ItemOf(row);
        ActivateWindow();
        CloseAll();
        _ = _vm.OpenItemAsync(item);
    }

    private void OnHeaderClicked(Level level)
    {
        var item = HeaderOf(level);
        ActivateWindow();
        CloseAll();
        _ = _vm.OpenItemAsync(item);
    }

    /// <summary>Right button held + left click on a row, then released: the row is shown in Explorer.</summary>
    private void OnRowExplorer(EntryRowViewModel row)
    {
        var item = ItemOf(row);
        ActivateWindow();
        CloseAll();
        _ = _vm.ShowInExplorerAsync(item);
    }

    /// <summary>
    /// A row as an item. In the cascade of an anchor outside the tab (a sidebar row), folders open
    /// in a folder tab of their own; in a card's cascade they navigate inside the tab.
    /// </summary>
    private ItemRef ItemOf(EntryRowViewModel row) => row.ToItemRef() with { NewTab = OpensInNewTab };

    private ItemRef HeaderOf(Level level) =>
        new(level.Model.Path, true, level.Model.Location, ItemContext.FlyoutHeader) { NewTab = OpensInNewTab };

    private bool OpensInNewTab => _anchorItem is { OpensInNewTab: true };

    // ---- dragging out to other apps -----------------------------------------------------------

    /// <summary>
    /// A row (or its preview) is dragged out: the cascade disappears (see <see cref="BeginDrag"/>),
    /// then the file or folder is handed to whatever it is dropped on. Blocks until the drag ends.
    /// </summary>
    private void DragOut(EntryRowViewModel row, string origin)
    {
        if (_dragging || !FileDragOut.CanDrag(row.Path) || FileDragOut.CreateData([row.Path], reorder: false) is not { } data)
        {
            return;
        }

        BeginDrag();
        try
        {
            FileDragOut.Run(_window, data, origin);
        }
        finally
        {
            EndDrag();
        }
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
        bool preferLeft = parent.Flyout is not null && parent.DirectionLeft; // keep going left once we went left
        OpenFlyout(parent.Index + 1, row.Path, row.Target, rowRect, anchor, preferLeft, row);
        parent.ActiveRow = row;
        row.IsActive = true;
    }

    /// <summary>
    /// Level 0 of a cascade opened from an anchor: the list of its folder, in a flyout, or for a file
    /// anchor (<see cref="ICascadeAnchor.File"/>) the file's preview in its place. For the
    /// placement <paramref name="hover"/> is the "row" (the list opens at its height) and
    /// <paramref name="placement"/> the "parent card" (the list goes beside it). A sidebar row is both.
    /// </summary>
    private void OpenRoot(FrameworkElement hover, FrameworkElement placement, ICascadeAnchor item)
    {
        if (!hover.IsVisible || !placement.IsVisible)
        {
            return;
        }

        Rect rowRect = ScreenRect(hover);
        Rect anchorRect = ReferenceEquals(placement, hover) ? rowRect : ScreenRect(placement);
        if (item.File is { } file)
        {
            OpenPreviewAt(-1, file.Row, file.Card.ToItemRef(), rowRect, anchorRect, preferLeft: false);
            return;
        }

        if (item.Target is not { } target)
        {
            return;
        }

        OpenFlyout(0, item.Path, target, rowRect, anchorRect, preferLeft: false, parentRow: null);
        item.IsHoverHeld = true;
    }

    /// <summary>Opens the flyout of level <paramref name="index"/> next to <paramref name="anchor"/>, at the height of <paramref name="rowRect"/>.</summary>
    private void OpenFlyout(int index, string path, NavLocation target, Rect rowRect, Rect anchor, bool preferLeft, EntryRowViewModel? parentRow)
    {
        var monitor = ShellInterop.GetMonitorArea(new Point(rowRect.Left + (rowRect.Width / 2), rowRect.Top + (rowRect.Height / 2)));

        var model = new EntryListViewModel(path, target, _services.Icons);
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

        var level = new Level(index, model, view.List)
        {
            Flyout = view,
            Popup = popup,
            ParentRow = parentRow,
            RowRect = rowRect,
            AnchorRect = anchor,
            PreferLeft = preferLeft,
        };
        _levels.Add(level);
        Wire(level);
        if (index == 0)
        {
            WatchForeground();
        }

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

        var placed = Place(level.Popup, level.Flyout, level.Flyout.CardElement, FlyoutView.CardWidth, FlyoutView.ShadowMargin, level.Index, level.RowRect, level.AnchorRect, level.PreferLeft);
        if (placed is not { } p)
        {
            return;
        }

        level.TargetX = p.X;
        level.TargetY = p.Y;
        level.DirectionLeft = p.Left;

        if (reveal)
        {
            level.Flyout.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, HoverTimings.FlyoutFadeIn));
        }

        // WPF may re-place the popup on its own (DPI change, screen clamping): correct it once more.
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () => VerifyPosition(p.Source, _levels.Contains(level) ? (level.TargetX, level.TargetY) : null));
    }

    /// <summary>
    /// Moves a popup whose visible card is <paramref name="cardWidth"/> DIP wide next to
    /// <paramref name="a"/> (the parent's card), level <paramref name="index"/> of the cascade.
    /// </summary>
    private (HwndSource Source, int X, int Y, bool Left)? Place(
        Popup popup, FrameworkElement root, FrameworkElement card, double cardWidth, double shadowMargin, int index, Rect row, Rect a, bool preferLeft)
    {
        if (PresentationSource.FromVisual(root) is not HwndSource source)
        {
            return null;
        }

        var monitor = ShellInterop.GetMonitorArea(new Point(row.Left + (row.Width / 2), row.Top + (row.Height / 2)));
        double s = monitor.Scale;
        var work = monitor.Work;
        double w = cardWidth * s;
        double h = Math.Max(1, card.ActualHeight) * s;
        double margin = shadowMargin * s;
        double gap = (index == 1 ? CardGap : FlyoutGap) * s;

        double rightX = a.Right + gap;
        double leftX = a.Left - gap - w;
        bool rightFits = rightX + w <= work.Right;
        bool leftFits = leftX >= work.Left;
        double x;
        bool left;
        if (preferLeft)
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
        if (Math.Abs(popup.HorizontalOffset - offX) > 0.5)
        {
            popup.HorizontalOffset = offX;
        }

        if (Math.Abs(popup.VerticalOffset - offY) > 0.5)
        {
            popup.VerticalOffset = offY;
        }

        ShellInterop.MoveWindow(source.Handle, wx, wy);

        // Which way did it actually go? Judge from the real window rectangle and pass it down.
        if (ShellInterop.GetWindowRect(source.Handle) is { } actual)
        {
            double center = actual.Left + (actual.Width / 2);
            left = center < a.Left + (a.Width / 2);
        }

        return (source, wx, wy, left);
    }

    /// <summary>Moves the popup window back to <paramref name="target"/> if WPF moved it (null = it closed).</summary>
    private static void VerifyPosition(HwndSource source, (int X, int Y)? target)
    {
        if (target is not { } t || source.IsDisposed)
        {
            return;
        }

        if (ShellInterop.GetWindowRect(source.Handle) is { } actual
            && (Math.Abs(actual.Left - t.X) > 1 || Math.Abs(actual.Top - t.Y) > 1))
        {
            ShellInterop.MoveWindow(source.Handle, t.X, t.Y);
        }
    }

    private static Rect ScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    // ---- previews (previewable file rows) -----------------------------------------------------

    /// <summary>The pointer moved to <paramref name="row"/> (null = blank space) of <paramref name="level"/>.</summary>
    private void OnPreviewHover(Level level, EntryRowViewModel? row)
    {
        // This level's preview stays while the pointer is on its row; elsewhere it closes after T_grace.
        if (_preview is { } preview && preview.ParentIndex == level.Index)
        {
            if (ReferenceEquals(row, preview.Row))
            {
                _previewCloseTimer.Stop();
            }
            else if (!_previewCloseTimer.IsEnabled)
            {
                Restart(_previewCloseTimer);
            }
        }

        if (row is { IsPreviewable: true } && !ReferenceEquals(_preview?.Row, row))
        {
            // With a child flyout open, wait T_grace as for any file row (rule 2), so moving across
            // this row towards the child does not replace the child with a preview.
            SchedulePreview(level, row, byKey: false, level.ActiveRow is null ? HoverTimings.Preview : HoverTimings.Grace);
        }
        else if (_pendingPreview is { } pending && pending.Level == level.Index && !pending.ByKey)
        {
            _previewTimer.Stop();
            _pendingPreview = null;
        }
    }

    private void SchedulePreview(Level level, EntryRowViewModel row, bool byKey, TimeSpan delay)
    {
        _pendingPreview = new PendingPreview(level.Index, row, byKey);
        _previewTimer.Interval = delay;
        Restart(_previewTimer);
    }

    private void OnPreviewTimer()
    {
        _previewTimer.Stop();
        if (_nativeMenu)
        {
            return; // frozen under Explorer's menu
        }

        if (_pendingPreview is not { } p)
        {
            return;
        }

        _pendingPreview = null;
        if (p.Level >= _levels.Count)
        {
            return;
        }

        var level = _levels[p.Level];
        bool stillThere = p.ByKey ? ReferenceEquals(level.KeyRow, p.Row) : ReferenceEquals(level.List.HoveredRow, p.Row);
        if (!stillThere || IsInsideDeeper(p.Level) || ReferenceEquals(_preview?.Row, p.Row))
        {
            return;
        }

        OpenPreview(level, p.Row);
    }

    private void OnPreviewCloseTimer()
    {
        _previewCloseTimer.Stop();
        if (_nativeMenu)
        {
            return; // frozen under Explorer's menu
        }

        if (_preview is not { } preview || preview.View.IsMouseOver)
        {
            return;
        }

        if (preview.ParentIndex >= 0 && preview.ParentIndex < _levels.Count && ReferenceEquals(_levels[preview.ParentIndex].List.HoveredRow, preview.Row))
        {
            return;
        }

        ClosePreview();
    }

    private void OpenPreview(Level parent, EntryRowViewModel row)
    {
        CloseFrom(parent.Index + 1); // the preview takes the child's place
        var container = parent.List.ContainerOf(row);
        if (container is null || !container.IsVisible)
        {
            return;
        }

        Rect rowRect = ScreenRect(container);
        Rect anchor = parent.Flyout is { } pf ? ScreenRect(pf.CardElement) : ScreenRect(parent.Card!);
        OpenPreviewAt(parent.Index, row, ItemOf(row), rowRect, anchor, preferLeft: parent.Flyout is not null && parent.DirectionLeft);
    }

    /// <summary>
    /// Opens the preview of <paramref name="row"/> beside level <paramref name="parentIndex"/>, or
    /// beside an anchor in place of level 0 when it is -1. <paramref name="item"/> is what its click
    /// and menu act on.
    /// </summary>
    private void OpenPreviewAt(int parentIndex, EntryRowViewModel row, ItemRef item, Rect rowRect, Rect anchor, bool preferLeft)
    {
        var monitor = ShellInterop.GetMonitorArea(new Point(rowRect.Left + (rowRect.Width / 2), rowRect.Top + (rowRect.Height / 2)));

        var model = new PreviewViewModel(row);
        var view = new PreviewView { DataContext = model };
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

        var preview = new Preview(parentIndex, row, model, view, popup)
        {
            Item = item,
            RowRect = rowRect,
            AnchorRect = anchor,
            PreferLeft = preferLeft,

            // Nothing of its own to show (audio, video, PDF…), only maybe a shell thumbnail: it
            // appears once a picture has come, and without one nothing appears at all.
            AwaitsPicture = row.PreviewKind == PreviewKind.None,
        };
        _preview = preview;
        if (preview.AwaitsPicture)
        {
            view.IsHitTestVisible = false; // invisible until then: the pointer goes to what is under it
        }
        else
        {
            MarkSource(preview, true);
        }

        view.MouseEnter += (_, _) =>
        {
            if (!ReferenceEquals(_preview, preview))
            {
                return;
            }

            // Reached the preview: keep it, and let nothing pending on its list replace it (nor a
            // neighbouring anchor crossed on the way, as in OnFlyoutEnter).
            if (_pendingAnchor is not null)
            {
                ClearPendingAnchor();
                _overlayTimer.Stop();
            }

            _exitTimer.Stop();
            _previewCloseTimer.Stop();
            if (_pendingPreview is { } pp && pp.Level == preview.ParentIndex)
            {
                _previewTimer.Stop();
                _pendingPreview = null;
            }

            if (_pendingOpen is { } po && po.Level == preview.ParentIndex)
            {
                _openTimer.Stop();
                _pendingOpen = null;
            }
        };
        view.MouseLeave += (_, _) => Restart(_exitTimer);
        view.Clicked += _ => OnPreviewClicked(preview);
        view.ContextMenuRequested += _ => ShowMenu(preview.Item, view.CardElement);
        view.DragRequested += _ => DragOut(row, "preview");
        view.CardElement.SizeChanged += (_, _) => PositionPreview(preview, reveal: false);

        // Rough position first (main window DPI), exact position once the popup has a size.
        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        double guessX = (preview.PreferLeft ? anchor.Left - ((PreviewView.CardWidth + PreviewView.ShadowMargin) * scale) : anchor.Right) / scale;
        popup.HorizontalOffset = guessX;
        popup.VerticalOffset = (rowRect.Top / scale) - RowLead - PreviewView.ShadowMargin;
        popup.IsOpen = true;
        if (parentIndex < 0)
        {
            WatchForeground(); // opened over an inactive window, as level 0 in OpenFlyout
        }

        _ = LoadPreviewAsync(preview, scale);
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PositionPreview(preview, reveal: true));
    }

    private void PositionPreview(Preview preview, bool reveal)
    {
        if (!ReferenceEquals(_preview, preview))
        {
            return;
        }

        var placed = Place(preview.Popup, preview.View, preview.View.CardElement, PreviewView.CardWidth, PreviewView.ShadowMargin, preview.ParentIndex + 1, preview.RowRect, preview.AnchorRect, preview.PreferLeft);
        if (placed is not { } p)
        {
            return;
        }

        preview.TargetX = p.X;
        preview.TargetY = p.Y;
        if (reveal && !preview.IsRevealed && !preview.AwaitsPicture)
        {
            preview.IsRevealed = true;
            MarkSource(preview, true);
            preview.View.IsHitTestVisible = true;
            preview.View.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, HoverTimings.FlyoutFadeIn));
        }

        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () => VerifyPosition(p.Source, ReferenceEquals(_preview, preview) ? (preview.TargetX, preview.TargetY) : null));
    }

    private async Task LoadPreviewAsync(Preview preview, double dpiScale)
    {
        var row = preview.Row;
        var kind = row.PreviewKind;
        int pixels = ThumbnailService.Bucket(PreviewView.ImageWidth * dpiScale);
        try
        {
            // Word / Excel / text: the text itself first. A document without text (or a "text"
            // file that turns out binary, like a .ts video) falls back to a picture below.
            string? emptyNote = null;
            if (kind is PreviewKind.WordDocument or PreviewKind.Spreadsheet or PreviewKind.Text)
            {
                var excerpt = await _previewTexts.GetAsync(row.Path, kind, row.ModifiedUtc, row.Length, preview.Cts.Token);
                if (!ReferenceEquals(_preview, preview))
                {
                    return;
                }

                if (ShowExcerpt(preview.Model, excerpt))
                {
                    return;
                }

                emptyNote = excerpt switch
                {
                    WordExcerpt => "本文がありません",
                    SheetExcerpt => "空のシート",
                    _ => null,
                };
            }

            // A picture: the shell's thumbnail (images, Office's or another app's handler, video
            // frames) or, for Open XML files, the thumbnail saved inside the file.
            var result = await _services.Thumbnails.GetAsync(row.Path, ThumbSource.File, pixels, ThumbnailService.IconPixelsBase, row.ModifiedUtc, row.Length, preview.Cts.Token);
            if (!ReferenceEquals(_preview, preview))
            {
                return;
            }

            // An icon means the shell had no picture for it: say so rather than show the icon big.
            if (result.Image is not null && result.Mode is (ThumbMode.Image or ThumbMode.Shell))
            {
                preview.Model.Show(result.Image);
                if (preview.AwaitsPicture)
                {
                    // The pointer (or the key selection) left the row while the picture was coming:
                    // don't flash a card that the grace timer is about to close.
                    bool wanted = !_previewCloseTimer.IsEnabled
                        && (preview.ParentIndex < 0
                            ? _anchor?.IsMouseOver == true && ReferenceEquals(_anchorItem?.File?.Row, preview.Row)
                            : preview.ParentIndex < _levels.Count
                                && (ReferenceEquals(_levels[preview.ParentIndex].List.HoveredRow, preview.Row)
                                    || ReferenceEquals(_levels[preview.ParentIndex].KeyRow, preview.Row)));
                    if (!wanted)
                    {
                        ClosePreview();
                        return;
                    }

                    // The picture has come: place the card at its real size, then fade it in.
                    preview.AwaitsPicture = false;
                    _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PositionPreview(preview, reveal: true));
                }
            }
            else
            {
                FailPreview(preview, emptyNote);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Preview failed: {row.Path}", ex);
            if (ReferenceEquals(_preview, preview))
            {
                FailPreview(preview, null);
            }
        }
    }

    /// <summary>
    /// Nothing to show: says why, or, for a row that only hoped for a shell thumbnail (audio
    /// without cover art…), closes the still invisible preview without a word.
    /// </summary>
    private void FailPreview(Preview preview, string? reason)
    {
        if (preview.AwaitsPicture)
        {
            ClosePreview();
        }
        else
        {
            preview.Model.Fail(reason);
        }
    }

    /// <summary>Shows an extracted excerpt; false when there is none to show (the caller tries a picture).</summary>
    private static bool ShowExcerpt(PreviewViewModel model, object? excerpt)
    {
        switch (excerpt)
        {
            case WordExcerpt { IsEmpty: false } document:
                model.ShowParagraphs(document);
                return true;
            case SheetExcerpt { IsEmpty: false } sheet:
                model.ShowSheet(sheet);
                return true;
            case TextExcerpt { IsEmpty: false } text:
                model.ShowLines(text);
                return true;
            case TextExcerpt:
                model.Fail("空のファイル");
                return true;
            default:
                return false;
        }
    }

    private void ClosePreview()
    {
        _previewCloseTimer.Stop();
        if (_preview is not { } preview)
        {
            return;
        }

        _preview = null;
        MarkSource(preview, false);
        preview.Dispose();
    }

    /// <summary>
    /// Highlights (or no longer) what the preview was opened from: its row, and for a preview beside
    /// an anchor the anchor's item too, so that the details row stays highlighted while the pointer
    /// is in the preview.
    /// </summary>
    private void MarkSource(Preview p, bool on)
    {
        p.Row.IsActive = on;
        if (p.ParentIndex < 0 && _anchorItem is { } a)
        {
            a.IsHoverHeld = on;
        }
    }

    /// <summary>
    /// A click on a preview: the file opens. A preview beside a details row goes through the card,
    /// as a click on the row does (nothing happens for a busy or disabled item).
    /// </summary>
    private void OnPreviewClicked(Preview preview)
    {
        var item = preview.Item;
        ActivateWindow();
        CloseAll();
        _ = item.Card is { } card ? _vm.ActivateCardAsync(card) : _vm.OpenItemAsync(item);
    }

    // ---- keyboard (§9.9) ----------------------------------------------------------------------

    /// <summary>Handles ↑ ↓ → ← Enter Esc while the cascade is open.</summary>
    public bool HandleKey(Key key)
    {
        if (_levels.Count == 0)
        {
            // At most a preview beside an anchor: Esc closes it, the other keys go to the view.
            return key == Key.Escape && CloseDeepest();
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

        // Selecting a previewable file with the keyboard previews it too.
        if (row.IsPreviewable)
        {
            if (!ReferenceEquals(_preview?.Row, row))
            {
                SchedulePreview(level, row, byKey: true, HoverTimings.Preview);
            }
        }
        else
        {
            if (_pendingPreview is { } pending && pending.Level == level.Index)
            {
                _previewTimer.Stop();
                _pendingPreview = null;
            }

            if (_preview is { } preview && preview.ParentIndex == level.Index)
            {
                ClosePreview();
            }
        }
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

    /// <summary>
    /// Starts the wait before level 0 opens. Over an inactive window the pointer has to rest longer,
    /// so that crossing a FolderDeck that shows behind another app opens nothing.
    /// </summary>
    private void RestartOverlay()
    {
        _overlayTimer.Interval = _window.IsActive ? HoverTimings.Overlay : HoverTimings.OverlayInactive;
        Restart(_overlayTimer);
    }

    /// <summary>
    /// Level 0 opened while the main window is inactive: no Deactivated will come to close it, and
    /// the popups are topmost windows, so they would stay over whatever app the user switches to.
    /// Watches the foreground window instead and closes when it changes. (Popups never become the
    /// foreground window, so the cascade does not close itself.)
    /// </summary>
    private void WatchForeground()
    {
        if (_window.IsActive)
        {
            return; // Deactivated closes the cascade
        }

        _foregroundAtOpen = ShellInterop.GetForegroundWindow();
        Restart(_foregroundTimer);
    }

    private void OnForegroundTimer()
    {
        if (_nativeMenu)
        {
            _foregroundTimer.Stop(); // frozen under Explorer's menu, which has the foreground
            return;
        }

        if (!IsOpen || _window.IsActive)
        {
            _foregroundTimer.Stop(); // closed, or activated by a click: Deactivated takes over
            return;
        }

        nint now = ShellInterop.GetForegroundWindow();
        if (now == 0)
        {
            return; // between two windows: decide on the next tick
        }

        if (_foregroundAtOpen == 0)
        {
            _foregroundAtOpen = now;
        }
        else if (now != _foregroundAtOpen)
        {
            CloseAll();
        }
    }

    /// <summary>
    /// A click inside the cascade. The popups do not take activation (WS_EX_NOACTIVATE), so a
    /// cascade opened over the inactive main window would otherwise open tabs, dialogs and menus in
    /// a window that is not in front (and a context menu would not close on an outside click).
    /// </summary>
    private void ActivateWindow()
    {
        if (!_window.IsActive)
        {
            _window.Activate();
        }
    }

    private void ShowMenu(ItemRef item, FrameworkElement target)
    {
        ActivateWindow();
        _menus.Show(item, target);
    }

    private sealed record PendingOpen(int Level, EntryRowViewModel Row);

    private sealed record PendingPreview(int Level, EntryRowViewModel Row, bool ByKey);

    /// <summary>
    /// An open preview: beside level <see cref="ParentIndex"/>, for one of its rows, or (-1) beside
    /// an anchor, in place of level 0.
    /// </summary>
    private sealed class Preview(int parentIndex, EntryRowViewModel row, PreviewViewModel model, PreviewView view, Popup popup) : IDisposable
    {
        public int ParentIndex { get; } = parentIndex;

        public EntryRowViewModel Row { get; } = row;

        public PreviewViewModel Model { get; } = model;

        public PreviewView View { get; } = view;

        public Popup Popup { get; } = popup;

        /// <summary>What a click and the menu of the preview act on.</summary>
        public ItemRef Item { get; init; } = null!;

        public Rect RowRect { get; init; }

        public Rect AnchorRect { get; init; }

        public bool PreferLeft { get; init; }

        /// <summary>
        /// Kept invisible (and transparent to the pointer) until a picture comes; when none comes
        /// it closes instead of saying it cannot be shown.
        /// </summary>
        public bool AwaitsPicture { get; set; }

        /// <summary>Faded in (once).</summary>
        public bool IsRevealed { get; set; }

        public int TargetX { get; set; }

        public int TargetY { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public void Dispose()
        {
            Cts.Cancel();
            Popup.IsOpen = false;
            Popup.Child = null;
        }
    }

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
