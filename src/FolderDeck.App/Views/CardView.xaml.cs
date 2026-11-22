using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using FolderDeck.Shell;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>
/// Where the views of a tab's items (cards, rows of the details list) report pointer and click events: the main window.
/// Hovering is reported with the element, because the cascade hangs on it; everything else is
/// reported with the item, so that any view of it can raise it.
/// </summary>
public interface ICardHost
{
    void CardMouseEnter(CardView card);

    void CardMouseLeave(CardView card);

    /// <summary>
    /// The pointer entered the name of an item shown as a row. <paramref name="nameCell"/> is both
    /// what is hovered and what the folder's list is placed beside.
    /// </summary>
    void RowMouseEnter(FrameworkElement nameCell, CardViewModel vm);

    /// <summary>
    /// The pointer moved on that name with no button held (and no press on the row since it came
    /// there): after a list was closed under a resting pointer, a real move asks for it again.
    /// </summary>
    void RowMouseMove(FrameworkElement nameCell, CardViewModel vm);

    /// <summary>The pointer left the name, or the element now shows another item.</summary>
    void RowMouseLeave(FrameworkElement nameCell);

    /// <summary>The item was clicked (decided on the release), with <paramref name="modifiers"/> held.</summary>
    void ItemClick(CardViewModel vm, FrameworkElement element, ModifierKeys modifiers);

    /// <summary>The item was right-clicked: its menu opens on <paramref name="element"/>.</summary>
    void ItemContextMenu(CardViewModel vm, FrameworkElement element);

    void ItemRemove(CardViewModel vm);

    /// <summary>The 📌 of a pinned item was clicked: unpins it.</summary>
    void ItemUnpin(CardViewModel vm);

    /// <summary>
    /// The item was left-clicked with the right button held and the right button was then released:
    /// shows it in Explorer (instead of opening it or showing its menu).
    /// </summary>
    void ItemShowInExplorer(CardViewModel vm);

    /// <summary>
    /// True when the item may be dragged at all (to other apps; at the top level also to a new
    /// position). Missing / not responding items cannot: their press stays a click.
    /// </summary>
    bool CanDragItem(CardViewModel vm);

    /// <summary>The pointer moved past the drag threshold with the button held: runs the drag (blocks until it ends).</summary>
    void ItemDrag(CardViewModel vm);
}

public partial class CardView : UserControl
{
    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(120));

    private readonly DropShadowEffect _hoverShadow = new()
    {
        BlurRadius = 14,
        ShadowDepth = 3,
        Direction = 270,
        Opacity = 0.22,
        Color = Colors.Black,
    };

    private CardViewModel? _vm;
    private CardViewModel? _realized;
    private CardGrid? _grid;
    private bool _hot;

    /// <summary>
    /// Clicks, the drag threshold and "right button held + left click" (the card is then shown in
    /// Explorer when the right button is released). Rows of the overlay do all of that for
    /// themselves, in their list: a press on them never drags the card.
    /// </summary>
    private readonly ItemPointerTracker _pointer;

    public CardView()
    {
        InitializeComponent();
        _hoverShadow.Freeze();
        _pointer = new ItemPointerTracker(this, () => _vm, IsOnOverlay);

        // Tunnelling, handled or not: a right press anywhere on the card (also on an overlay row,
        // which handles it) starts afresh.
        AddHandler(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler((_, _) => _pointer.RightDown()), handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Realize();
        Unloaded += (_, _) => Unrealize();
        ThumbArea.SizeChanged += (_, e) => UpdateClip(e.NewSize);
        SizeChanged += (_, _) => UpdateThumbSize();
    }

    public static ICardHost? Host { get; set; }

    public CardViewModel? ViewModel => _vm;

    /// <summary>The thumbnail area (the overlay covers exactly this, §9.2).</summary>
    public FrameworkElement ThumbAreaElement => ThumbArea;

    public void SetOverlay(UIElement? overlay) => OverlayHost.Content = overlay;

    public UIElement? CurrentOverlay => OverlayHost.Content as UIElement;

    public void RefreshHot() => UpdateHot();

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHot();
        Host?.CardMouseEnter(this);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointer.Leave(e); // left the card with the button held: that is a drag too
        UpdateHot();
        Host?.CardMouseLeave(this);
    }

    // The press is not marked handled here (only the gesture's is): see ItemPointerTracker.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pointer.LeftDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer.Move(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _pointer.LeftUp(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _pointer.RightUp(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is { } vm)
        {
            Host?.ItemRemove(vm);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is { } vm)
        {
            Host?.ItemUnpin(vm);
        }
    }

    /// <summary>A press on the rows of the overlay belongs to their list.</summary>
    private bool IsOnOverlay(DependencyObject? source) =>
        CurrentOverlay is not null && FindAncestor<OverlayView>(source) is not null;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        Unrealize();
        OverlayHost.Content = null;
        _pointer.ItemChanged();
        _vm = e.NewValue as CardViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        UpdateAll();
        if (IsLoaded)
        {
            Realize();
        }
    }

    private void Realize()
    {
        if (_vm is null || ReferenceEquals(_realized, _vm))
        {
            return;
        }

        Unrealize();
        _grid ??= FindAncestor<CardGrid>(this);
        _realized = _vm;
        _vm.Realize(_grid?.ThumbPixels ?? 256, _grid?.IconPixels ?? 96);
    }

    private void Unrealize()
    {
        _realized?.Unrealize();
        _realized = null;
    }

    private void UpdateThumbSize()
    {
        if (_realized is not null && _grid is not null)
        {
            _realized.UpdateThumbSize(_grid.ThumbPixels, _grid.IconPixels);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CardViewModel.Thumbnail):
            case nameof(CardViewModel.ThumbMode):
                UpdateThumb();
                break;
            case nameof(CardViewModel.Kind):
                UpdateAll();
                break;
            case nameof(CardViewModel.BadgeMode):
            case nameof(CardViewModel.CountsLoaded):
            case nameof(CardViewModel.ErrorBadgeText):
                UpdateBadges();
                break;
            case nameof(CardViewModel.IsHoverHeld):
                UpdateHot();
                break;
            case nameof(CardViewModel.IsDragged):
            case nameof(CardViewModel.IsCut):
                UpdateOpacity();
                break;
        }
    }

    private void UpdateAll()
    {
        UpdateThumb();
        UpdateBadges();
        UpdateKind();
        UpdateHot();
    }

    private void UpdateThumb()
    {
        var vm = _vm;
        var mode = vm?.Thumbnail is null ? ThumbMode.None : vm.ThumbMode;
        ThumbImage.Visibility = mode == ThumbMode.Image ? Visibility.Visible : Visibility.Collapsed;
        ShellImage.Visibility = mode == ThumbMode.Shell ? Visibility.Visible : Visibility.Collapsed;
        IconImage.Visibility = mode == ThumbMode.Icon ? Visibility.Visible : Visibility.Collapsed;
        WarnGlyph.Visibility = vm?.IsDisabled == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBadges()
    {
        var mode = _vm?.BadgeMode ?? BadgeMode.Pending;
        BadgePending.Visibility = mode == BadgeMode.Pending ? Visibility.Visible : Visibility.Collapsed;
        BadgeFolder.Visibility = mode == BadgeMode.Folder ? Visibility.Visible : Visibility.Collapsed;
        BadgeFile.Visibility = mode == BadgeMode.File ? Visibility.Visible : Visibility.Collapsed;
        bool warn = mode is BadgeMode.Missing or BadgeMode.NoResponse or BadgeMode.Error;
        BadgeWarn.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = mode switch
        {
            BadgeMode.Missing => "見つかりません",
            BadgeMode.NoResponse => "応答がありません",
            BadgeMode.Error => _vm?.ErrorBadgeText ?? "読み込めません",
            _ => string.Empty,
        };
    }

    private void UpdateKind()
    {
        bool disabled = _vm?.IsDisabled == true;
        NameText.TextDecorations = disabled ? TextDecorations.Strikethrough : null;
        UpdateOpacity();
        Cursor = disabled ? Cursors.Arrow : Cursors.Hand;
        PinButton.IsEnabled = !disabled; // like the menu: a missing item cannot be unpinned from its card
        UpdateCloseButton();
    }

    private void UpdateOpacity()
    {
        var vm = _vm;
        // Being dragged wins over a pending cut (dimmed like Explorer's), which wins over missing.
        Root.Opacity = vm?.IsDragged == true ? 0.4 : vm?.IsCut == true ? 0.5 : vm?.IsDisabled == true ? 0.6 : 1.0;
    }

    private void UpdateHot()
    {
        bool hot = _vm is not null && !_vm.IsDisabled && (IsMouseOver || _vm.IsHoverHeld);
        UpdateCloseButton();
        if (hot == _hot)
        {
            return;
        }

        _hot = hot;
        if (hot)
        {
            CardBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            CardBorder.Effect = _hoverShadow;
        }
        else
        {
            CardBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            CardBorder.Effect = null;
        }

        Lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hot ? -1 : 0, HoverDuration));
    }

    private void UpdateCloseButton()
    {
        var vm = _vm;
        bool show = vm is not null && vm.CanRemove && (vm.IsDisabled || IsMouseOver || vm.IsHoverHeld);
        CloseButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClip(Size size)
    {
        // Round only the top corners of the thumbnail (the bottom edge is extended out of view).
        ThumbArea.Clip = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height + 12), 9, 9);
    }

    internal static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
