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

/// <summary>Where cards report pointer and click events (the main window).</summary>
public interface ICardHost
{
    void CardMouseEnter(CardView card);

    void CardMouseLeave(CardView card);

    void CardActivate(CardView card);

    void CardContextMenu(CardView card);

    void CardRemove(CardView card);
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
    private bool _pressed;
    private bool _hot;

    public CardView()
    {
        InitializeComponent();
        _hoverShadow.Freeze();
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
        _pressed = false;
        UpdateHot();
        Host?.CardMouseLeave(this);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // A double click must not run twice (§6.8): only the first press counts.
        _pressed = e.ClickCount == 1;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressed && IsMouseOver)
        {
            _pressed = false;
            e.Handled = true;
            Host?.CardActivate(this);
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        Host?.CardContextMenu(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Host?.CardRemove(this);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        Unrealize();
        OverlayHost.Content = null;
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
        Root.Opacity = disabled ? 0.6 : 1.0;
        Cursor = disabled ? Cursors.Arrow : Cursors.Hand;
        UpdateCloseButton();
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
