using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FolderDeck.Views;

/// <summary>
/// Themed name input (登録ペインの登録・名前の変更): a title, a label and a text box that starts with
/// <c>initial</c> selected. OK stays disabled while the text is blank; <see cref="Result"/> is the
/// trimmed text after OK, null otherwise.
/// </summary>
public partial class NameInputDialog : Window
{
    public NameInputDialog(string title, string body, string initial)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        NameBox.Text = initial;
        UpdateOk();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        SourceInitialized += (_, _) => (Application.Current as App)?.Theme?.ApplyTitleBar(this);

        // An Enter still held from what opened the dialog (a menu item chosen with Enter) repeats into
        // it: only a new press answers. Other keys repeat as usual (holding Backspace in the box).
        PreviewKeyDown += (_, e) =>
        {
            if (e.IsRepeat && e.Key == Key.Enter)
            {
                e.Handled = true;
            }
        };
    }

    /// <summary>The trimmed name after OK; null while the dialog is open or after キャンセル.</summary>
    public string? Result { get; private set; }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdateOk();

    private void UpdateOk() => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string text = NameBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Result = text;
        DialogResult = true;
    }
}
