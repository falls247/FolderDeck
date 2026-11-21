using System.Windows;
using FolderDeck.ViewModels;

namespace FolderDeck.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => (Application.Current as App)?.Theme?.ApplyTitleBar(this);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_vm.Validate())
        {
            DialogResult = true;
        }
    }
}
