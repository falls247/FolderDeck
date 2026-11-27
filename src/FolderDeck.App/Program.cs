using FolderDeck.Shell;

namespace FolderDeck;

/// <summary>
/// The entry point (<c>StartupObject</c>; the Main that WPF generates for App.xaml is not used). A
/// second launch only signals the running copy and quits here, before WPF starts: with 常駐モード
/// the window then appears as soon as this small process got that far (§4.4).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main()
    {
        var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            instance.SignalFirstInstance();
            instance.Dispose();
            return 0;
        }

        var app = new App { Instance = instance };
        app.InitializeComponent();
        return app.Run();
    }
}
