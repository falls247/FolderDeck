using System.Windows;
using System.Windows.Input;

namespace FolderDeck.Views;

/// <summary>
/// The list of every keyboard shortcut (F1 / Ctrl + /, SOW §3.3-2), from <see cref="ShortcutCatalog"/>.
/// Modal, as the other dialogs: while it is open no key reaches the main window. Closed by Esc (the
/// close button is IsCancel), F1 and Ctrl + / again, or the close button.
/// </summary>
public partial class ShortcutHelpWindow : Window
{
    public ShortcutHelpWindow()
    {
        InitializeComponent();
        GroupList.ItemsSource = ShortcutCatalog.Groups;
        Loaded += (_, _) => ListScroller.Focus();
        SourceInitialized += (_, _) => (Application.Current as App)?.Theme?.ApplyTitleBar(this);
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>F1 / Ctrl + / (as in <see cref="IsToggleKey"/>) close it again.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // A key still held from what opened the list repeats into it: only a new press closes it.
        if (e.IsRepeat)
        {
            e.Handled = IsToggleKey(e);
            return;
        }

        if (IsToggleKey(e))
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>
    /// F1, or Ctrl + / (the main keyboard's "/" key and the keypad's). The main key is VK_OEM_2,
    /// Key.OemQuestion, on both the US and the Japanese (JIS) layout: on JIS it is the key right of
    /// "." that types "/" (and "・" / "め"), so the same Key works there.
    /// </summary>
    public static bool IsToggleKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return (key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
            || ((key is Key.OemQuestion or Key.Divide) && Keyboard.Modifiers == ModifierKeys.Control);
    }
}
