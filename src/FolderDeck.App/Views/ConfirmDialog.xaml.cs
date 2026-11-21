using System.Windows;

namespace FolderDeck.Views;

/// <summary>Themed confirmation (登録解除, 実行形式, 履歴のクリア…).</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string body, string? path, string primaryText, bool danger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        if (string.IsNullOrEmpty(path))
        {
            PathBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            PathText.Text = path;
        }

        PrimaryButton.Content = primaryText;
        if (danger)
        {
            PrimaryButton.Style = (Style)FindResource("Btn.Danger");
        }

        // Risky actions start with the safe choice focused; Enter alone never confirms them.
        Loaded += (_, _) =>
        {
            if (danger)
            {
                CancelButton.Focus();
            }
            else
            {
                PrimaryButton.IsDefault = true;
                PrimaryButton.Focus();
            }
        };
        SourceInitialized += (_, _) => (Application.Current as App)?.Theme?.ApplyTitleBar(this);
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
}
