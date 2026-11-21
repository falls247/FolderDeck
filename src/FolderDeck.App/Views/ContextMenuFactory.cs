using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>The app's own right-click menu (§11), shared by cards, overlay rows, flyout rows and headers.</summary>
public sealed class ContextMenuFactory
{
    private readonly MainViewModel _vm;

    public ContextMenuFactory(MainViewModel vm)
    {
        _vm = vm;
    }

    /// <summary>Raised when a menu opens / closes (the cascade stays open meanwhile, §9.6 rule 8).</summary>
    public event Action? MenuOpened;

    public event Action? MenuClosed;

    /// <summary>Called before a menu command runs that leaves the cascade (open, navigate…).</summary>
    public Action? BeforeNavigate { get; set; }

    public void Show(ItemRef item, FrameworkElement target)
    {
        var card = item.Card;
        bool disabled = card?.IsDisabled == true;
        bool topCard = card?.IsTopLevel == true;
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.MousePoint,
        };

        menu.Items.Add(Item("開く", "", !disabled, () =>
        {
            BeforeNavigate?.Invoke();
            _ = card is not null ? _vm.ActivateCardAsync(card) : _vm.OpenItemAsync(item);
        }));
        menu.Items.Add(Item("エクスプローラーで表示", "", !disabled, () => _vm.ShowInExplorer(item)));
        if (item.IsFolder)
        {
            menu.Items.Add(Item("エクスプローラーで開く", "", !disabled, () => _vm.OpenInExplorer(item)));
        }

        menu.Items.Add(Item("パスをコピー", "", !disabled, () => _vm.CopyPath(item)));
        menu.Items.Add(new Separator());

        bool pinned = _vm.IsPinned(item.Path);
        menu.Items.Add(Item(pinned ? "ピン留めを外す" : "ピン留めする", pinned ? "" : "", !disabled, () => _vm.TogglePin(item)));
        if (item.IsFolder)
        {
            menu.Items.Add(Item("登録フォルダに追加", "", !disabled, () => _ = _vm.AddToRegisteredAsync(item)));
        }

        if (topCard && card is not null)
        {
            switch (card.OwnerTab)
            {
                case TabKind.Registered:
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Item("登録を解除", "", true, () => _vm.Unregister(card)));
                    break;
                case TabKind.Recent:
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Item("履歴から外す", "", true, () => _vm.ExcludeFromRecent(card)));
                    break;
                case TabKind.Frequent when !pinned:
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Item("一覧から外す", "", true, () => _vm.RemoveFromFrequent(card)));
                    break;
            }
        }

        menu.Opened += (_, _) => MenuOpened?.Invoke();
        menu.Closed += (_, _) => MenuClosed?.Invoke();
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string glyph, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, Tag = glyph, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
}
