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

        // The 登録フォルダ row of the sidebar this menu is for (null for cards, rows and headers).
        var registered = item.Context == ItemContext.Sidebar ? _vm.FindRegistered(item.Path) : null;
        bool disabled = card?.IsDisabled == true || registered?.IsDisabled == true;
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
        menu.Items.Add(Item("エクスプローラーで表示", "", !disabled, () => _ = _vm.ShowInExplorerAsync(item)));
        menu.Items.Add(Item("パスをコピー", "", !disabled, () => _vm.CopyPath(item)));
        menu.Items.Add(new Separator());

        bool pinned = _vm.IsPinned(item.Path);
        menu.Items.Add(Item(pinned ? "ピン留めを外す" : "ピン留めする", pinned ? "" : "", !disabled, () => _vm.TogglePin(item)));
        if (item.IsFolder && item.Context != ItemContext.Sidebar && !_vm.IsRegistered(item.Path))
        {
            // Any folder that is not on the sidebar yet (a card of 最近の履歴 / よく使う項目, a card or
            // row inside a folder, a flyout header) can be put there.
            menu.Items.Add(Item("登録フォルダに追加", "", !disabled, () => _ = _vm.AddToRegisteredAsync(item)));
        }

        if (MainViewModel.CanDelete(item))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("削除（ごみ箱へ移動）", "", true, () =>
            {
                BeforeNavigate?.Invoke(); // the confirmation and the reload replace the cascade
                _ = _vm.DeleteAsync(item);
            }));
        }

        if (registered is not null)
        {
            // Also for a missing folder: that is how it gets off the sidebar.
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("登録を解除", "", true, () =>
            {
                BeforeNavigate?.Invoke();
                _vm.Unregister(registered);
            }));
        }

        if (topCard && card is not null)
        {
            switch (card.OwnerTab)
            {
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
