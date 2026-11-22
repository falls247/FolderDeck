using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FolderDeck.Core.Paths;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// The edit box of an item's name (F2, 名前の変更, a new folder), like Explorer's: a TextBox over the
/// name of one realized item view, at most one at a time. It only edits text; the window renames
/// (<see cref="Committed"/>).
/// <para>
/// The box lives in an <see cref="Adorner"/> of the name element, so it sits in the adorner layer of
/// the list's scroll viewer: it scrolls with the item and is clipped to the list, and its mouse
/// input does not go through the item view (no click, drag or item menu of the card under it). The
/// window must still tell its own handlers apart from a press in the box (<see cref="Contains"/>):
/// they see the press on the way up.
/// </para>
/// <para>
/// It ends once, whatever ends it first: Enter (commit, unless the name cannot be used: then it says
/// why and stays), Esc (cancel), the keyboard focus going elsewhere in the window or a press outside
/// it (<see cref="CommitIfOutside"/>), and the item view being unloaded or showing another item
/// (scrolled away, the list reloaded or rebuilt): commit. Focus moving into the box's own context
/// menu, or the window being deactivated, ends nothing (WPF gives the focus back to the box).
/// </para>
/// </summary>
public sealed class RenameEditor
{
    /// <summary>Lines a card's box may grow to before its text scrolls.</summary>
    private const int CardMaxLines = 6;

    /// <summary>About one line of the box's 12.5 px text (style RenameBox) at 100 % (it follows <see cref="TextScale"/>).</summary>
    private const double LineHeight = 17;

    /// <summary>Height of the box over a details row's name at 100 % (the row is 28).</summary>
    private const double RowBoxHeight = 24;

    private readonly Window _window;
    private readonly Action<string> _say;
    private Session? _session;

    /// <param name="window">The window the item views are in (its focus and activation).</param>
    /// <param name="say">Shows a short notice (a name that cannot be used).</param>
    public RenameEditor(Window window, Action<string> say)
    {
        _window = window;
        _say = say;
    }

    /// <summary>The box closed with a commit: the card and the text as typed (not cleaned). Raised after <see cref="Ended"/>.</summary>
    public event Action<CardViewModel, string>? Committed;

    /// <summary>
    /// The box closed: true for a commit (<see cref="Committed"/> follows), false for a cancel.
    /// Raised once per <see cref="Begin"/>, before <see cref="Committed"/>.
    /// </summary>
    public event Action<bool>? Ended;

    public bool IsOpen => _session is not null;

    /// <summary>
    /// Opens the box over the name of <paramref name="itemView"/> (a realized <see cref="CardView"/>
    /// or <see cref="DetailsRowView"/> showing <paramref name="card"/>) with <paramref name="text"/>,
    /// the part before the extension selected for a file. <paramref name="typeAhead"/>: text typed
    /// before the box existed, put in place of the selection once the box has the focus. False
    /// when the view has no adorner layer.
    /// </summary>
    public bool Begin(FrameworkElement itemView, CardViewModel card, string text, string? typeAhead = null)
    {
        End(commit: true);

        FrameworkElement adorned;
        Func<Rect> place;
        bool wrap;
        switch (itemView)
        {
            case CardView view:
                // Over the two lines of the name, a little wider; it grows downwards with the text.
                var name = view.NameText;
                adorned = name;
                place = () => new Rect(-4, -3, name.ActualWidth + 8, name.ActualHeight + 6);
                wrap = true;
                break;
            case DetailsRowView row:
                // From where the name starts to the end of the name column (over the pin).
                var column = row.NameColumn;
                var rowName = row.NameText;
                adorned = column;
                place = () =>
                {
                    double x = Math.Max(0, rowName.TranslatePoint(default, column).X - 4);
                    double height = Math.Min(TextScale.RowHeight(RowBoxHeight, TextScale.RenameFont), column.ActualHeight);
                    return new Rect(x, (column.ActualHeight - height) / 2, Math.Max(60, column.ActualWidth - x - 4), height);
                };
                wrap = false;
                break;
            default:
                return false;
        }

        if (AdornerLayer.GetAdornerLayer(adorned) is not { } layer)
        {
            return false;
        }

        var box = new TextBox
        {
            Text = text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = wrap ? VerticalAlignment.Top : VerticalAlignment.Center,
            MaxLength = ItemNames.MaxLength,
            MaxHeight = wrap ? (CardMaxLines * LineHeight * TextScale.Factor) + 6 : double.PositiveInfinity,
        };
        box.SetResourceReference(FrameworkElement.StyleProperty, "RenameBox");

        var adorner = new BoxAdorner(adorned, box, place);
        string pending = new((typeAhead ?? string.Empty).Where(c => !ItemNames.IsInvalidChar(c)).ToArray());
        var session = new Session(card, itemView, box, adorner, layer, ItemNames.EditSelection(text, card.IsFolderLike), pending);
        _session = session;

        box.PreviewKeyDown += OnBoxPreviewKeyDown;
        box.KeyDown += OnBoxKeyDown;
        box.PreviewTextInput += OnBoxPreviewTextInput;
        box.LostKeyboardFocus += OnBoxLostKeyboardFocus;
        itemView.DataContextChanged += OnItemViewDataContextChanged;
        itemView.Unloaded += OnItemViewUnloaded;
        layer.Add(adorner);

        // After the box is laid out (a box that is not measured yet takes no focus).
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusBox(session));
        return true;
    }

    /// <summary>The element is the box or inside it (a press the window's handlers must leave to it).</summary>
    public bool Contains(object? source) => _session is { } s && IsWithin(source as DependencyObject, s.Box);

    /// <summary>A press anywhere in the window that is not in the box commits (Explorer does the same).</summary>
    public void CommitIfOutside(object? source)
    {
        if (_session is not null && !Contains(source))
        {
            End(commit: true);
        }
    }

    /// <summary>Commits now, wherever the focus is.</summary>
    public void Commit() => End(commit: true);

    private void FocusBox(Session session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        // From the menu of Explorer the window may not be active yet: it takes the focus when it is.
        if (!_window.IsActive)
        {
            _window.Activate();
        }

        if (session.Box.Focus())
        {
            SelectStart(session);
            return;
        }

        void OnActivated(object? sender, EventArgs e)
        {
            _window.Activated -= OnActivated;
            if (ReferenceEquals(_session, session) && session.Box.Focus())
            {
                SelectStart(session);
            }
        }

        _window.Activated += OnActivated;
    }

    /// <summary>The selection the box starts with, replaced by what was typed before it opened (as in Explorer).</summary>
    private static void SelectStart(Session session)
    {
        var box = session.Box;
        box.Select(session.Selection.Start, session.Selection.Length);
        if (session.TypeAhead.Length > 0)
        {
            box.SelectedText = session.TypeAhead;
            box.Select(session.Selection.Start + session.TypeAhead.Length, 0);
        }
    }

    private void OnBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // During IME composition Enter and Esc arrive as ImeProcessed: they confirm or cancel the
        // composition, not the edit.
        switch (e.Key)
        {
            case Key.Enter or Key.Escape when e.IsRepeat:
                e.Handled = true; // a held key ends one box, not the one that opens again after it
                break;
            case Key.Enter:
                e.Handled = true;
                if (_session is { } s)
                {
                    string name = ItemNames.Clean(s.Box.Text);
                    var problem = ItemNames.Check(name);
                    if (name.Length > 0 && problem != NameProblem.None)
                    {
                        _say(ItemNames.MessageOf(problem)); // stays open, as Explorer's balloon
                        return;
                    }
                }

                End(commit: true);
                break;
            case Key.Escape:
                e.Handled = true;
                End(commit: false);
                break;
        }
    }

    /// <summary>Keys the box did not use must not scroll the list from under it.</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
        }
    }

    /// <summary>A character no name may contain is refused as it is typed, with the reason (as Explorer does).</summary>
    private void OnBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Any(ItemNames.IsInvalidChar))
        {
            e.Handled = true;
            _say(ItemNames.MessageOf(NameProblem.InvalidCharacter));
        }
    }

    private void OnBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_session is not { } s)
        {
            return;
        }

        if (e.NewFocus is DependencyObject next && (IsWithin(next, s.Box) || IsInContextMenu(next)))
        {
            return; // the box's own parts, or its menu (切り取り / コピー / 貼り付け): focus comes back
        }

        if (e.NewFocus is null && !_window.IsActive)
        {
            return; // another window took over: WPF gives the focus back on return
        }

        End(commit: true);
    }

    private void OnItemViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => End(commit: true);

    private void OnItemViewUnloaded(object sender, RoutedEventArgs e) => End(commit: true);

    private void End(bool commit)
    {
        if (_session is not { } s)
        {
            return;
        }

        // First of all: whatever this sets off (the focus moving, the box leaving the tree) ends nothing more.
        _session = null;
        s.Box.PreviewKeyDown -= OnBoxPreviewKeyDown;
        s.Box.KeyDown -= OnBoxKeyDown;
        s.Box.PreviewTextInput -= OnBoxPreviewTextInput;
        s.Box.LostKeyboardFocus -= OnBoxLostKeyboardFocus;
        s.ItemView.DataContextChanged -= OnItemViewDataContextChanged;
        s.ItemView.Unloaded -= OnItemViewUnloaded;

        bool hadFocus = s.Box.IsKeyboardFocusWithin;
        string text = s.Box.Text;
        s.Layer.Remove(s.Adorner);
        if (hadFocus && _window.IsActive)
        {
            FocusWindowItself(_window); // the keys go to the window again (not to a box that is gone)
        }

        Ended?.Invoke(commit);
        if (commit)
        {
            Committed?.Invoke(s.Card, text);
        }
    }

    /// <summary>
    /// Gives the keyboard focus to the window itself, not to the element it focused last (the box
    /// that is gone, a toolbar button): the window is a focus scope, and focusing it alone hands
    /// the focus on to that element.
    /// </summary>
    internal static void FocusWindowItself(Window window)
    {
        FocusManager.SetFocusedElement(window, null);
        Keyboard.Focus(window);
    }

    private static bool IsInContextMenu(DependencyObject node)
    {
        for (DependencyObject? n = node; n is not null; n = Parent(n))
        {
            if (n is ContextMenu)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (DependencyObject? n = node; n is not null; n = Parent(n))
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The visual parent, or the logical one for what is not a visual (text elements).</summary>
    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private sealed record Session(
        CardViewModel Card, FrameworkElement ItemView, TextBox Box, Adorner Adorner, AdornerLayer Layer, (int Start, int Length) Selection, string TypeAhead);

    /// <summary>
    /// Holds the box over its element: <c>place</c> gives the box's rectangle in the element's
    /// coordinates (its height is the least height; a wrapping box grows to its text). Not clipped
    /// to the element, so the box may be larger than the name it covers.
    /// </summary>
    private sealed class BoxAdorner : Adorner
    {
        private readonly TextBox _box;
        private readonly Func<Rect> _place;

        public BoxAdorner(UIElement adorned, TextBox box, Func<Rect> place)
            : base(adorned)
        {
            _box = box;
            _place = place;
            AddVisualChild(box);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _box;

        protected override Size MeasureOverride(Size constraint)
        {
            Rect rect = _place();
            _box.Measure(new Size(Math.Max(0, rect.Width), double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Rect rect = _place();
            double height = Math.Max(rect.Height, _box.DesiredSize.Height);
            _box.Arrange(new Rect(rect.X, rect.Y, Math.Max(0, rect.Width), height));
            return finalSize;
        }
    }
}
