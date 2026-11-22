using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

/// <summary>The app's own right-click menu (§11), shared by cards, overlay rows, flyout rows, headers and sidebar rows.</summary>
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

    /// <summary>
    /// The menu of one item, or with <paramref name="group"/> (more than one item: the selection the
    /// clicked item is part of, in display order) the short menu of the whole group: 切り取り,
    /// コピー and パスをコピー. The caller decides the group (cascade rows and headers have none).
    /// </summary>
    public void Show(ItemRef item, FrameworkElement target, IReadOnlyList<ItemRef>? group = null)
    {
        if (group is { Count: > 1 })
        {
            ShowGroup(group, target);
            return;
        }

        var card = item.Card;

        // The 登録フォルダ row of the sidebar this menu is for (null for cards, rows and headers).
        var registered = item.Context == ItemContext.Sidebar ? _vm.FindRegistered(item.Path) : null;
        bool disabled = card?.IsDisabled == true || registered?.IsDisabled == true || item.IsDisabled;

        // A drive of the PC section: open, Explorer, copy the path, register. A drive is not pinned,
        // deleted or unregistered. One that is not connected can still be shown in Explorer (which
        // offers to reconnect it) and have its path copied; only opening it here is off.
        bool drive = item.Context == ItemContext.Drive;
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
        menu.Items.Add(Item("エクスプローラーで表示", "", !disabled || drive, () => _ = _vm.ShowInExplorerAsync(item)));
        menu.Items.Add(Item("パスをコピー", "", !disabled || drive, () => _vm.CopyPath(item)));
        AddClipboardItems(menu, [item], item);

        bool pinned = _vm.IsPinned(item.Path);
        bool canRegister = item.IsFolder && item.Context != ItemContext.Sidebar && !_vm.IsRegistered(item.Path);
        if (!drive || canRegister)
        {
            menu.Items.Add(new Separator());
        }

        if (!drive)
        {
            menu.Items.Add(Item(pinned ? "ピン留めを外す" : "ピン留めする", pinned ? "" : "", !disabled, () => _vm.TogglePin(item)));
        }

        if (canRegister)
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

        Open(menu);
    }

    /// <summary>The menu of the blank area of the item list: 貼り付け (into the folder shown) and 最新の情報に更新.</summary>
    public void ShowBlank(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        string? folder = _vm.PasteFolderOf(null);
        menu.Items.Add(Item("貼り付け", PasteGlyph, _vm.CanPaste(folder), () => _ = _vm.PasteAsync(folder!)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("最新の情報に更新", RefreshGlyph, true, () => _vm.RefreshCommand.Execute(null)));
        Open(menu);
    }

    /// <summary>Several selected items: only what applies to all of them at once.</summary>
    private void ShowGroup(IReadOnlyList<ItemRef> group, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        AddClipboardItems(menu, group, pasteItem: null, separator: false);
        menu.Items.Add(Item("パスをコピー", CopyGlyph, true, () => _vm.CopyPaths(group)));
        Open(menu);
    }

    /// <summary>
    /// 切り取り and コピー when every item allows them, and 貼り付け into <paramref name="pasteItem"/>
    /// when that is a folder (or a usable drive row), enabled while the clipboard offers files.
    /// </summary>
    private void AddClipboardItems(ContextMenu menu, IReadOnlyList<ItemRef> items, ItemRef? pasteItem, bool separator = true)
    {
        bool cut = items.All(MainViewModel.CanCut);
        bool copy = items.All(MainViewModel.CanCopy);
        string? pasteFolder = pasteItem is null ? null : _vm.PasteFolderOf(pasteItem);
        if (!cut && !copy && pasteFolder is null)
        {
            return;
        }

        if (separator)
        {
            menu.Items.Add(new Separator());
        }

        if (cut)
        {
            menu.Items.Add(Item("切り取り", CutGlyph, true, () => _vm.CopyToClipboard(items, cut: true)));
        }

        if (copy)
        {
            menu.Items.Add(Item("コピー", CopyGlyph, true, () => _vm.CopyToClipboard(items, cut: false)));
        }

        if (pasteFolder is not null)
        {
            menu.Items.Add(Item("貼り付け", PasteGlyph, _vm.CanPaste(pasteFolder), () =>
            {
                BeforeNavigate?.Invoke(); // the transfer reloads the list the cascade hangs on
                _ = _vm.PasteAsync(pasteFolder);
            }));
        }
    }

    private void Open(ContextMenu menu)
    {
        menu.Opened += (_, _) => MenuOpened?.Invoke();
        menu.Closed += (_, _) => MenuClosed?.Invoke();
        menu.IsOpen = true;
    }

    // Segoe Fluent Icons: Cut, Copy (also パスをコピー), Paste, Refresh.
    private const string CutGlyph = "\uE8C6";
    private const string CopyGlyph = "\uE8C8";
    private const string PasteGlyph = "\uE77F";
    private const string RefreshGlyph = "\uE72C";

    private static MenuItem Item(string header, string glyph, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, Tag = glyph, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
}
