using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

public partial class SettingsWindow : Window
{
    /// <summary>The tab shown last: the next opening (in this run) starts there.</summary>
    private static int s_lastPage;

    private readonly SettingsViewModel _vm;
    private readonly RadioButton[] _tabs;
    private readonly FrameworkElement[] _pages;
    private int _page = -1;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _tabs = [TabView, TabActions, TabFolders, TabHistory, TabData, TabPanes];
        _pages = [PageView, PageActions, PageFolders, PageHistory, PageData, PagePanes];
        ShowPage(s_lastPage);
        PreviewKeyDown += OnWindowKeyDown;
        SourceInitialized += (_, _) => (Application.Current as App)?.Theme?.ApplyTitleBar(this);
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        int index = Array.IndexOf(_tabs ?? [], sender);
        if (index >= 0)
        {
            ShowPage(index);
        }
    }

    /// <summary>Shows one page; the others stay laid out (Hidden) so the window keeps its height.</summary>
    private void ShowPage(int index)
    {
        index = Math.Clamp(index, 0, _pages.Length - 1);
        if (index == _page)
        {
            return;
        }

        _page = index;
        s_lastPage = index;
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
        }

        _tabs[index].IsChecked = true;
        PageScroller.ScrollToTop();
    }

    /// <summary>Ctrl+Tab / Ctrl+PageDown: the next tab; with Shift / Ctrl+PageUp: the previous one.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Tab or Key.PageDown or Key.PageUp)
        {
            bool back = e.Key == Key.PageUp || (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            ShowPage((_page + (back ? _pages.Length - 1 : 1)) % _pages.Length);
            _tabs[_page].Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Enter in the name of a new 登録ペイン registers it (when it can be) and never presses OK:
    /// handled here, before the default button sees the key.
    /// </summary>
    private void OnPaneNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_vm.RegisterPanesCommand.CanExecute(null))
        {
            _vm.RegisterPanesCommand.Execute(null);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_vm.Validate())
        {
            DialogResult = true;
            return;
        }

        // The invalid field may be on another tab: show it, with its text selected.
        (int page, TextBox box)? field = _vm.ErrorField switch
        {
            nameof(SettingsViewModel.AutoRefreshSecondsText) => (2, IntervalBox),
            nameof(SettingsViewModel.RecentLimitText) => (3, RecentBox),
            nameof(SettingsViewModel.FrequentLimitText) => (3, FrequentBox),
            nameof(SettingsViewModel.ThumbCacheMbText) => (4, CacheBox),
            _ => null,
        };
        if (field is { } f)
        {
            ShowPage(f.page);
            f.box.Focus();
            f.box.SelectAll();
        }
    }
}
