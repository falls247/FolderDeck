using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Input;
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
/// <para>
/// Menu aim (§9.6 rule 9): while the pointer is on its way from a list to the child flyout or
/// preview open beside it, the rows it crosses neither open their child nor close the open one,
/// and the gap does not count as outside (see <see cref="IsHeadingForChildOf"/>).
/// </para>
/// </summary>
public sealed class CascadeController
{
    /// <summary>
    /// Space (DIP) between what a flyout or preview is placed beside (the card, the parent flyout,
    /// an anchor) and its visible card. Beside a card it used to be 8 (§9.5): 4 everywhere keeps the
    /// way from the overlay to its child short.
    /// <para>
    /// The popup window starts <see cref="FlyoutView.ShadowMargin"/> before its card, so it reaches
    /// over the parent's edge, and a layered window takes the pointer wherever its pixels are not
    /// fully transparent (the shadow): there is no dead zone between the two, and a smaller gap would
    /// only let the shadow take more of the right end of the parent's rows.
    /// </para>
    /// </summary>
    private const double FlyoutGap = 4;

    /// <summary>How far (DIP) beyond the top and bottom of a child's card a pointer still counts as heading for it.</summary>
    private const double AimSlack = 4;

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

    /// <summary>Recent pointer moves over the main window and the cascade's popups (see <see cref="OnPointerMoved"/>).</summary>
    private readonly MenuAim _aim = new();

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
    /// <summary>
    /// Another card the pointer entered while heading for the open cascade's child (menu aim): the
    /// cascade moves there (rule 4) only when the pointer is no longer on its way (see <see cref="OnOverlayTimer"/>).
    /// Never set together with <see cref="_pendingAnchor"/> on purpose, but both are handled.
    /// </summary>
    private CardView? _pendingCard;
    private CardViewModel? _waitingFor;
    private PendingOpen? _pendingOpen;
    private PendingPreview? _pendingPreview;
    private Preview? _preview;
    private int _graceLevel = -1;
    private int _keyLevel = -1;
    private bool _menuOpen;
    /// <summary>Nothing opens on hover: during a drag, Explorer's menu or a name's edit box (always <see cref="AnySuspension"/>).</summary>
    private bool _suspended;
    private bool _dragging;

    /// <summary>A name's edit box is open (see <see cref="SetEditing"/>).</summary>
    private bool _editing;

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
        TrackPointer(_window);
    }

    /// <summary>
    /// Something of the cascade is showing. A preview without a level under it only exists beside
    /// an anchor (a file row of the details list, <see cref="ICascadeAnchor.File"/>).
    /// </summary>
    public bool IsOpen => _levels.Count > 0 || _preview is not null;

    /// <summary>A menu of the app or Explorer's menu is open (the cascade is frozen meanwhile).</summary>
    public bool IsMenuOpen => _menuOpen || _nativeMenu;

    /// <summary>A hover is about to open something (an overlay, a flyout, a preview): a replaced list would leave it on a discarded element.</summary>
    public bool IsHoverPending => _overlayTimer.IsEnabled || _openTimer.IsEnabled || _previewTimer.IsEnabled;

    /// <summary>For the <c>display:</c> log line: what is open and what would keep <see cref="CloseAll"/> from closing it.</summary>
    public string DiagnosticState => $"cascade open {IsOpen}, levels {_levels.Count}, nativeMenu {_nativeMenu}, dragging {_dragging}";

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
        _suspended = AnySuspension;
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
        _suspended = AnySuspension;
    }

    /// <summary>
    /// The edit box of an item's name opens (true) or closes (false). While it is open the cascade
    /// is closed and no hover opens anything (cards, rows of the details list, sidebar rows): a list
    /// or a preview would cover the items, and Esc must reach the edit box. <see cref="CloseAll"/>
    /// keeps working. A card under a pointer that has not moved opens again only after the pointer
    /// re-enters it (as after a drag).
    /// </summary>
    public void SetEditing(bool editing)
    {
        if (editing)
        {
            CloseAll(); // does nothing during a drag or Explorer's menu…
            StopTimers(); // …so the timers are stopped in every case
        }

        _editing = editing;
        _suspended = AnySuspension;
    }

    private bool AnySuspension => _dragging || _nativeMenu || _editing;

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
            _suspended = AnySuspension;
            StopTimers();
            return;
        }

        if (!_nativeMenu)
        {
            return;
        }

        _nativeMenu = false;
        _suspended = AnySuspension;
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
            if (IsOpen && IsHeadingForAnyChild())
            {
                // Crossing another card on the way to the open child (menu aim; a diagonal way from a
                // low overlay row leaves the card through its bottom): rule 4 waits until the pointer
                // rests on that card (see OnOverlayTimer) or leaves it.
                _pendingCard = card;
                RestartOverlay();
                return;
            }

            CloseAll(); // rule 4
            _card = card;
        }

        _pendingCard = null; // back on the cascade's own card
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
        else if (ReferenceEquals(card, _pendingCard))
        {
            // Left the card it was crossing (into a flyout, back, or out): the exit decides as usual.
            _pendingCard = null;
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

    /// <summary>
    /// The elements of every anchor of type <typeparamref name="T"/> may have moved or gone without input
    /// (the tab strip scrolled, a tab was added, closed or moved): as <see cref="AnchorRemoved"/> for whichever
    /// of them the list hangs on or is waiting to switch to. Lists of other anchors (a sidebar row, a crumb) stay.
    /// </summary>
    public void AnchorsMoved<T>() where T : ICascadeAnchor
    {
        if (_pendingAnchorItem is T)
        {
            ClearPendingAnchor();
            _overlayTimer.Stop();
        }

        if (_anchorItem is T)
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
        _aim.Clear(); // the moves belong to this cascade (none are recorded while nothing is open)
        _generation++;
    }

    /// <summary>
    /// Closes a cascade that hangs on an item of the shown tab (a card), for reasons that only
    /// concern the tab's content (it scrolled, its items were rebuilt). A cascade opened from outside
    /// the tab (a sidebar row or a crumb: <see cref="ICascadeAnchor.OutlivesContent"/>) is not anchored
    /// to the content and stays: a tab that finishes loading must not take away the list the user is in.
    /// </summary>
    public void CloseCardCascade()
    {
        if (_anchorItem is not { OutlivesContent: true })
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
        _pendingCard = null;
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

        if ((_pendingCard is not null || _pendingAnchor is not null) && IsHeadingForAnyChild())
        {
            RestartOverlay(); // still on its way to the open child (menu aim): ask again later
            return;
        }

        if (_pendingCard is { } nextCard)
        {
            // The pointer is no longer heading for the child and is still on the card it was
            // crossing: rule 4, then that card's overlay as if it had just rested there.
            _pendingCard = null;
            if (_suspended || !nextCard.IsMouseOver)
            {
                return;
            }

            CloseAll();
            _card = nextCard;
            if (nextCard.ViewModel is not { IsFolderLike: true, IsDisabled: false })
            {
                return; // as CardMouseEnter: only folder cards get an overlay
            }
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
        level.List.RowDragRequested += (_, row) => DragOut(row, ItemOf(row), "row");

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
            TrackPointer(flyout); // its own window: the main window does not see these moves
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
        if (_pendingAnchor is not null || _pendingCard is not null)
        {
            // Reached the list: the anchor or card crossed on the way does not take the cascade.
            ClearPendingAnchor();
            _pendingCard = null;
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

        if (IsHeadingForChildOf(p.Level))
        {
            // Crossing this row on the way to the open child (menu aim): ask again later.
            _pendingOpen = p;
            Restart(_openTimer);
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
        // Still away from the active row (and not inside its child): close the child, unless the
        // pointer is on its way there (menu aim).
        if (level.ActiveRow is not null && !ReferenceEquals(level.List.HoveredRow, level.ActiveRow) && !IsInsideDeeper(index))
        {
            if (IsHeadingForChildOf(index))
            {
                _graceLevel = index;
                Restart(_graceTimer);
                return;
            }

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

        if (IsHeadingForAnyChild())
        {
            Restart(_exitTimer); // crossing the gap between a list and its child (menu aim)
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
    /// A row as an item. In the cascade of an anchor outside the tab (a sidebar row) or of a
    /// top-level card (最近の履歴・よく使う項目), folders open in a folder tab of their own; in the cascade
    /// of a card inside a folder they navigate inside the tab.
    /// </summary>
    private ItemRef ItemOf(EntryRowViewModel row) => row.ToItemRef() with { NewTab = FoldersOpenInNewTab };

    private ItemRef HeaderOf(Level level) =>
        new(level.Model.Path, true, level.Model.Location, ItemContext.FlyoutHeader) { NewTab = FoldersOpenInNewTab };

    /// <summary>The anchor is outside the tab (a sidebar row): the cascade also outlives the tab's content.</summary>
    private bool OpensInNewTab => _anchorItem is { OpensInNewTab: true };

    /// <summary>Where the cascade's folders open: <see cref="OpensInNewTab"/>, or the cascade hangs on a top-level card or row.</summary>
    private bool FoldersOpenInNewTab =>
        OpensInNewTab || _anchorItem is CardAnchor { IsTopLevel: true } || (_anchorItem is null && _card?.ViewModel?.IsTopLevel == true);

    // ---- dragging out to other apps -----------------------------------------------------------

    /// <summary>
    /// A row (or its preview) is dragged out: the cascade disappears (see <see cref="BeginDrag"/>),
    /// then the file or folder is handed to whatever it is dropped on (a folder of the app: moved or
    /// copied into it once the drag has ended). Move is offered when <paramref name="item"/> (the
    /// row, or the item the preview shows) can be cut. Blocks until the drag ends.
    /// </summary>
    private void DragOut(EntryRowViewModel row, ItemRef item, string origin)
    {
        if (_dragging || !FileDragOut.CanDrag(row.Path) || FileDragOut.CreateData([row.Path], reorder: false) is not { } data)
        {
            return;
        }

        var drag = new OutgoingDrag(data, [row.Path], moveAllowed: !_vm.IsFileOperationRunning && MainViewModel.CanCut(item), intoFolders: true);
        BeginDrag();
        try
        {
            FileDragOut.Run(_window, drag, origin);
        }
        finally
        {
            EndDrag();
        }

        _vm.FinishDrag(drag);
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

        Rect anchor = parent.Flyout is { } pf ? ScreenRect(pf.CardElement) : ScreenRect(parent.Card!);
        bool preferLeft = parent.Flyout is not null && parent.DirectionLeft; // keep going left once we went left
        OpenFlyout(parent.Index + 1, row.Path, row.Target, container, anchor, preferLeft, row);
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

        Rect anchorRect = ScreenRect(placement);
        if (item.File is { } file)
        {
            OpenPreviewAt(-1, file.Row, file.Card.ToItemRef(), hover, anchorRect, preferLeft: false);
            return;
        }

        if (item.Target is not { } target)
        {
            return;
        }

        OpenFlyout(0, item.Path, target, hover, anchorRect, preferLeft: false, parentRow: null, below: item.OpensBelow);
        item.IsHoverHeld = true;
    }

    /// <summary>
    /// Opens the flyout of level <paramref name="index"/> next to <paramref name="anchor"/>, at the
    /// height of <paramref name="origin"/> (the row it hangs at, also what its window is created on: <see cref="OpenOn"/>),
    /// or, when <paramref name="below"/> (<see cref="ICascadeAnchor.OpensBelow"/>), under <paramref name="anchor"/>.
    /// </summary>
    private void OpenFlyout(int index, string path, NavLocation target, FrameworkElement origin, Rect anchor, bool preferLeft, EntryRowViewModel? parentRow, bool below = false)
    {
        Rect rowRect = ScreenRect(origin);
        var monitor = MonitorOf(rowRect);

        // FoldersOnly narrows only the first list (the crumb's subfolders); deeper lists show files too, like any cascade.
        var model = new EntryListViewModel(path, target, _services.Icons, foldersOnly: index == 0 && _anchorItem is { FoldersOnly: true });
        var view = new FlyoutView { DataContext = model };
        double room = monitor.Work.Height; // physical pixels
        if (below)
        {
            // Under the element; when that room is too short and above is wider, Place puts it above.
            double under = monitor.Work.Bottom - anchor.Bottom;
            double over = anchor.Top - monitor.Work.Top;
            room = (under / monitor.Scale) - FlyoutGap - WorkMargin >= 120 || under >= over ? under : over;
        }

        view.CardMaxHeight = Math.Max(120, (room / monitor.Scale) - (below ? FlyoutGap : 0) - WorkMargin);
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
            Below = below,
        };
        _levels.Add(level);
        Wire(level);
        if (index == 0)
        {
            WatchForeground();
        }

        // Rough position first, exact position once the popup has a size. The offsets are DIPs of
        // the popup's own DPI, which is the row's monitor's (OpenOn), not the main window's.
        double scale = monitor.Scale;
        if (below)
        {
            popup.HorizontalOffset = (anchor.Left / scale) - FlyoutView.ShadowMargin;
            popup.VerticalOffset = (anchor.Bottom / scale) + FlyoutGap - FlyoutView.ShadowMargin;
        }
        else
        {
            double guessX = (level.PreferLeft ? anchor.Left - ((FlyoutView.CardWidth + FlyoutView.ShadowMargin) * scale) : anchor.Right) / scale;
            popup.HorizontalOffset = guessX;
            popup.VerticalOffset = (rowRect.Top / scale) - RowLead - FlyoutView.ShadowMargin;
        }

        OpenOn(popup, origin);

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
    /// the popup window directly. Moving the window does not change the DPI it draws with: that is
    /// settled when it opens (<see cref="OpenOn"/>).
    /// </summary>
    private void Position(Level level, bool reveal)
    {
        if (!_levels.Contains(level) || level.Popup is null || level.Flyout is null)
        {
            return;
        }

        var placed = Place(level.Popup, level.Flyout, level.Flyout.CardElement, FlyoutView.CardWidth, FlyoutView.ShadowMargin, level.RowRect, level.AnchorRect, level.PreferLeft, level.Below);
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
    /// <paramref name="a"/> (the parent's card, or the anchor), <see cref="FlyoutGap"/> away; when
    /// <paramref name="below"/>, under it with the left edges aligned (above it when the screen ends first).
    /// </summary>
    private (HwndSource Source, int X, int Y, bool Left)? Place(
        Popup popup, FrameworkElement root, FrameworkElement card, double cardWidth, double shadowMargin, Rect row, Rect a, bool preferLeft, bool below = false)
    {
        if (PresentationSource.FromVisual(root) is not HwndSource source)
        {
            return null;
        }

        var monitor = MonitorOf(row);
        double s = monitor.Scale;
        var work = monitor.Work;

        // What the popup really draws with (and reads its offsets in): its window's DPI from when it
        // opened, normally the monitor's (OpenOn). The gaps are distances on the monitor.
        double p = source.CompositionTarget?.TransformToDevice.M11 ?? s;
        double w = cardWidth * p;
        double h = Math.Max(1, card.ActualHeight) * p;
        double margin = shadowMargin * p;
        double gap = FlyoutGap * s;

        double x;
        double y;
        bool left;
        if (below)
        {
            x = Math.Clamp(a.Left, work.Left, Math.Max(work.Left, work.Right - w));
            y = a.Bottom + gap;
            if (y + h > work.Bottom)
            {
                double above = a.Top - gap - h;
                y = above >= work.Top ? above : Math.Max(work.Top, work.Bottom - h);
            }

            left = false; // deeper lists go right
        }
        else
        {
            double rightX = a.Right + gap;
            double leftX = a.Left - gap - w;
            bool rightFits = rightX + w <= work.Right;
            bool leftFits = leftX >= work.Left;
            if (preferLeft)
            {
                (x, left) = leftFits ? (leftX, true) : rightFits ? (rightX, false) : (a.Right - (OverlapShift * s) - w, true);
            }
            else
            {
                (x, left) = rightFits ? (rightX, false) : leftFits ? (leftX, true) : (a.Left + (OverlapShift * s), false);
            }

            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
            y = row.Top - (RowLead * s);
            if (y + h > work.Bottom)
            {
                y = work.Bottom - h;
            }

            if (y < work.Top)
            {
                y = work.Top;
            }
        }

        int wx = (int)Math.Round(x - margin);
        int wy = (int)Math.Round(y - margin);
        double offX = wx / p;
        double offY = wy / p;
        if (Math.Abs(popup.HorizontalOffset - offX) > 0.5)
        {
            popup.HorizontalOffset = offX;
        }

        if (Math.Abs(popup.VerticalOffset - offY) > 0.5)
        {
            popup.VerticalOffset = offY;
        }

        ShellInterop.MoveWindow(source.Handle, wx, wy);

        // Which way did it actually go? Judge from the real window rectangle and pass it down
        // (a list under its element has no side: its children go right).
        if (!below && ShellInterop.GetWindowRect(source.Handle) is { } actual)
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

    /// <summary>
    /// Opens a flyout's or preview's popup so that it draws with the DPI of <paramref name="origin"/>'s
    /// monitor. A WPF popup takes its DPI from the monitor its window is created on (a new window
    /// each time it opens) and keeps it while open: Popup.OnDpiChanged swallows WM_DPICHANGED, so
    /// moving the window afterwards (<see cref="Place"/>) does not rescale it. Without a
    /// PlacementTarget the window is created at (0,0), on the primary monitor: with a 200 % primary,
    /// lists opened on a 100 % monitor were drawn twice too large. With one, it is created on the
    /// monitor of the target's top-left (<see cref="MonitorOf"/>). The target serves only that and is
    /// cleared once the window exists (the position stays the offsets', Placement is Absolute), so
    /// the popup keeps no link to a row that may be recycled.
    /// WPF also makes the target's window the owner of the popup's window when the foreground
    /// window is in its owner chain (the main window, or for a deeper level the parent flyout, owned
    /// by the main window in turn). That is undone: an owned window dies with its owner, and so
    /// would the WPF menus and tooltips opened from a row inside a flyout (they would be owned by
    /// the flyout), without their Closed event when the flyout closes first (the menu's Closed is
    /// what clears <see cref="_menuOpen"/>). Unowned, the windows are as they were before.
    /// The callers measure <paramref name="origin"/> on the screen first (<see cref="ScreenRect"/>),
    /// so it has a window and WPF creates the popup's window here, synchronously.
    /// </summary>
    private static void OpenOn(Popup popup, FrameworkElement origin)
    {
        popup.PlacementTarget = origin;
        popup.IsOpen = true;
        popup.ClearValue(Popup.PlacementTargetProperty);
        if (popup.Child is { } child && PresentationSource.FromVisual(child) is HwndSource source)
        {
            ShellInterop.ClearOwner(source.Handle);
        }
    }

    /// <summary>
    /// The monitor of a popup opened at <paramref name="row"/> (screen rectangle of the
    /// <see cref="OpenOn"/> origin), whose work area holds it. WPF creates the window, and so fixes
    /// its DPI, on the monitor of the top-left truncated to whole pixels
    /// (PopupInitialPlacementHelper.GetPlacementOrigin). A row across two monitors (the window
    /// straddles them) still goes by its center, as it always did, when both have that DPI; when
    /// they differ it goes by the top-left, as the popup could not draw right on the other one.
    /// </summary>
    private static ShellInterop.MonitorArea MonitorOf(Rect row)
    {
        var created = ShellInterop.GetMonitorArea(new Point(Math.Truncate(row.Left), Math.Truncate(row.Top)));
        var center = ShellInterop.GetMonitorArea(new Point(row.Left + (row.Width / 2), row.Top + (row.Height / 2)));
        return center.Scale == created.Scale ? center : created;
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

        if (!p.ByKey && IsHeadingForChildOf(p.Level))
        {
            // Crossing this row on the way to the open child or preview (menu aim): ask again later.
            _pendingPreview = p;
            Restart(_previewTimer);
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

        if (IsHeadingForChildOf(preview.ParentIndex))
        {
            Restart(_previewCloseTimer); // on the way to the preview (menu aim)
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

        Rect anchor = parent.Flyout is { } pf ? ScreenRect(pf.CardElement) : ScreenRect(parent.Card!);
        OpenPreviewAt(parent.Index, row, ItemOf(row), container, anchor, preferLeft: parent.Flyout is not null && parent.DirectionLeft);
    }

    /// <summary>
    /// Opens the preview of <paramref name="row"/> beside level <paramref name="parentIndex"/>, or
    /// beside an anchor in place of level 0 when it is -1, at the height of <paramref name="origin"/>
    /// (the row's element, as in <see cref="OpenFlyout"/>). <paramref name="item"/> is what its click
    /// and menu act on.
    /// </summary>
    private void OpenPreviewAt(int parentIndex, EntryRowViewModel row, ItemRef item, FrameworkElement origin, Rect anchor, bool preferLeft)
    {
        Rect rowRect = ScreenRect(origin);
        var monitor = MonitorOf(rowRect);

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
            if (_pendingAnchor is not null || _pendingCard is not null)
            {
                ClearPendingAnchor();
                _pendingCard = null;
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
        TrackPointer(view);
        view.Clicked += _ => OnPreviewClicked(preview);
        view.ContextMenuRequested += _ => ShowMenu(preview.Item, view.CardElement);
        view.DragRequested += _ => DragOut(row, preview.Item, "preview");
        view.CardElement.SizeChanged += (_, _) => PositionPreview(preview, reveal: false);

        // Rough position first, exact position once the popup has a size (as in OpenFlyout).
        double scale = monitor.Scale;
        double guessX = (preview.PreferLeft ? anchor.Left - ((PreviewView.CardWidth + PreviewView.ShadowMargin) * scale) : anchor.Right) / scale;
        popup.HorizontalOffset = guessX;
        popup.VerticalOffset = (rowRect.Top / scale) - RowLead - PreviewView.ShadowMargin;
        OpenOn(popup, origin);
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

        var placed = Place(preview.Popup, preview.View, preview.View.CardElement, PreviewView.CardWidth, PreviewView.ShadowMargin, preview.RowRect, preview.AnchorRect, preview.PreferLeft);
        if (placed is not { } p)
        {
            return;
        }

        preview.TargetX = p.X;
        preview.TargetY = p.Y;
        preview.DirectionLeft = p.Left;
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
            // frames) or, for Open XML files, the thumbnail saved inside the file. An online-only
            // picture without one is downloaded first (ThumbnailService.DownloadAsync); closing the
            // preview cancels its token and so the download.
            bool downloadStarted = false;
            var downloading = new Progress<ThumbResult>(_ =>
            {
                downloadStarted = true; // reported here (UI thread) before the result comes back
                if (ReferenceEquals(_preview, preview))
                {
                    preview.Model.ShowDownloading();
                }
            });
            var result = await _services.Thumbnails.GetAsync(
                row.Path, ThumbSource.File, pixels, ThumbnailService.IconPixelsBase, row.ModifiedUtc, row.Length, preview.Cts.Token, downloading, urgent: true);
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
                string? note = emptyNote ?? await CloudNoteAsync(row, downloadStarted, _services.Thumbnails);
                if (ReferenceEquals(_preview, preview))
                {
                    FailPreview(preview, note);
                }
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
    /// Why an online-only picture shows nothing (null when it is not one, or for no particular
    /// reason): the app's downloads were refused (cancelled in Windows' notification, or the app
    /// blocked), it is too large to be downloaded for a preview, downloads are paused (offline), or
    /// its download failed. The attributes are read on a worker (a network path may be slow).
    /// </summary>
    private static async Task<string?> CloudNoteAsync(EntryRowViewModel row, bool downloadStarted, ThumbnailService thumbnails)
    {
        if (row.PreviewKind != PreviewKind.Image || !await Task.Run(() => FileContent.IsDownloadable(row.Path)))
        {
            return null;
        }

        return thumbnails.DownloadsRefused ? "ダウンロードが取り消されたかブロックされています（FolderDeck を起動し直すまでダウンロードしません）"
            : row.Length > ThumbnailService.MaxDownloadBytes ? "大きいファイルのためダウンロードしません"
            : thumbnails.DownloadsPaused ? "ダウンロードできません（オフライン、または同期アプリが動いていません）"
            : downloadStarted ? "ダウンロードできませんでした"
            : null;
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
                    return true;
                }

                return false; // no row chosen with the keys: Enter is the selection's (MainWindow.HandleSelectionKey closes the cascade)
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

    // ---- menu aim (§9.6 rule 9) ---------------------------------------------------------------
    //
    // On its way from a row to the child beside the list, the pointer usually crosses other rows
    // of the same list (and the gap): folder rows below the open one, since folders sort first.
    // Every decision about a list's rows and the exit is taken by a timer tick (open, grace, exit,
    // preview, preview close), never at once, so each of those ticks asks IsHeadingForChildOf
    // before it acts: while the pointer is heading for the child, the timer starts again instead,
    // and its own interval is the re-check. Once the pointer rests or turns away, the next tick
    // decides as before. No timer or state of its own: StopTimers, CloseAll and BeginDrag already
    // cover everything that waits, and a tick under _nativeMenu returns before it asks.
    // Switching to another card or anchor waits too: CardMouseEnter keeps the card in _pendingCard
    // (rule 4 is deferred, not dropped), and OnOverlayTimer restarts while either _pendingCard or
    // _pendingAnchor is set and the pointer is still heading for the child.

    private void TrackPointer(UIElement element) =>
        element.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnPointerMoved), handledEventsToo: true);

    /// <summary>
    /// A pointer move over the main window or one of the cascade's popups (each is its own window,
    /// so each is tracked), kept in screen pixels while something of the cascade is showing.
    /// </summary>
    private void OnPointerMoved(object sender, MouseEventArgs e)
    {
        if (IsOpen)
        {
            RecordPointer();
        }
    }

    private void RecordPointer()
    {
        if (ShellInterop.TryGetCursorPos(out int x, out int y))
        {
            _aim.Record(x, y, Environment.TickCount64);
        }
    }

    /// <summary>
    /// The pointer is on its way to what is open beside level <paramref name="index"/> (its child
    /// flyout or its preview; -1: beside the anchor): a decision about that level's rows (open
    /// another row's child, close the child, preview another row) waits, and so does the exit.
    /// False while it rests (it has not moved for <see cref="HoverTimings.AimMoving"/>), moves away
    /// or along the list, and when nothing visible is open there. Asked only when a decision is due.
    /// </summary>
    private bool IsHeadingForChildOf(int index)
    {
        if (_suspended)
        {
            return false; // a drag, Explorer's menu or an edit box: nothing waits (StopTimers ran)
        }

        if (index < 0 && _anchorItem is { OpensBelow: true })
        {
            return false; // the list is right under its element, nothing to cross (MenuAim knows side edges only)
        }

        FrameworkElement? card = null;
        bool left = false;
        if (index + 1 < _levels.Count && _levels[index + 1].Flyout is { } flyout)
        {
            card = flyout.CardElement;
            left = _levels[index + 1].DirectionLeft;
        }
        else if (_preview is { IsRevealed: true } preview && preview.ParentIndex == index)
        {
            card = preview.View.CardElement;
            left = preview.DirectionLeft;
        }

        if (card is null || !card.IsVisible || PresentationSource.FromVisual(card) is null)
        {
            return false;
        }

        RecordPointer(); // where it is now, also when it was over a window that is not ours
        Rect target = ScreenRect(card);
        double slack = AimSlack * VisualTreeHelper.GetDpi(_window).DpiScaleX;
        return _aim.IsHeadingFor(
            target.Left, target.Top, target.Right, target.Bottom, left,
            Environment.TickCount64, (long)HoverTimings.AimMoving.TotalMilliseconds, slack);
    }

    /// <summary>Outside the card and every list: on the way to one of the children (the exit waits).</summary>
    private bool IsHeadingForAnyChild()
    {
        for (int i = -1; i < _levels.Count; i++)
        {
            if (IsHeadingForChildOf(i))
            {
                return true;
            }
        }

        return false;
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

        /// <summary>It went to the left of what it is placed beside (as <see cref="Level.DirectionLeft"/>).</summary>
        public bool DirectionLeft { get; set; }

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

        /// <summary>Under its anchor rather than beside it (<see cref="ICascadeAnchor.OpensBelow"/>, level 0 only).</summary>
        public bool Below { get; init; }

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
