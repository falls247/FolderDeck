using System.Text;
using System.Windows;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Stores;
using FolderDeck.Services;
using FolderDeck.Shell;
using FolderDeck.ViewModels;
using FolderDeck.Views;

namespace FolderDeck;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private DateTime _lastErrorDialog = DateTime.MinValue;

    public ThemeManager? Theme { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // §4.4: a second launch only brings the running window to the front.
        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirst)
        {
            _instance.SignalFirstInstance();
            Shutdown(0);
            return;
        }

        string dataDir = ParseDataDir(e.Args) ?? DataStores.DefaultDataDirectory;
        AppLog.Initialize(Path.Combine(dataDir, "logs"));
        AppLog.Info($"FolderDeck {typeof(App).Assembly.GetName().Version} starting (data: {dataDir})");
        HookUnhandledExceptions();
        var startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        string Since() => $"{(DateTime.Now - startedAt).TotalMilliseconds:F0} ms";
        AppLog.Info($"startup: OnStartup at {Since()}");

        Theme = new ThemeManager(this);
        Theme.Apply();

        var stores = new DataStores(dataDir, SystemClock.Instance);
        await Task.Run(stores.LoadAll); // JSON I/O off the UI thread (§16)
        _services = new AppServices(stores);
        AppLog.Info($"startup: data loaded at {Since()}");

        var vm = new MainViewModel(_services);
        var window = new MainWindow(vm, Theme);
        window.RestorePlacement(stores.Settings.Current.Window);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        AppLog.Info($"startup: window built at {Since()}");
        window.ContentRendered += (_, _) => AppLog.Info($"startup: first frame at {Since()}");
        window.Show();

        foreach (var store in stores.All)
        {
            store.SaveFailed += (s, ex) => Dispatcher.BeginInvoke(() => vm.Message.Error($"{s.FileName} を保存できませんでした：{ex.Message}"));
        }

        ReportLoadProblems(stores, vm.Message);
        _instance.Listen(() => Dispatcher.BeginInvoke(window.BringToFront));
        await vm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Stores.FlushAll();
        }
        finally
        {
            AppLog.Info("FolderDeck exiting");
            _services?.Dispose();
            Theme?.Dispose();
            _instance?.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary><c>--data-dir &lt;path&gt;</c> overrides <c>%LOCALAPPDATA%\FolderDeck</c> (development and testing).</summary>
    private static string? ParseDataDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return null;
    }

    private static void ReportLoadProblems(DataStores stores, MessageBarViewModel message)
    {
        var warnings = new StringBuilder();
        var errors = new StringBuilder();
        foreach (var store in stores.All)
        {
            switch (store.LoadStatus)
            {
                case LoadStatus.Recovered:
                    warnings.Append($"{store.FileName} が壊れていたため {Path.GetFileName(store.BackupPath)} に退避し、空のデータで続けます。");
                    break;
                case LoadStatus.Failed:
                    errors.Append($"{store.FileName} を読み込めないため、この内容は保存しません（ファイルは上書きしません）：{store.LoadDetail}");
                    break;
            }
        }

        if (errors.Length > 0)
        {
            message.Error(errors.ToString() + warnings);
        }
        else if (warnings.Length > 0)
        {
            message.Warning(warnings.ToString());
        }
    }

    // ---- §16: unhandled exceptions are logged and reported, never fatal on the UI thread ------

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
            ShowUnexpected(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
            Dispatcher.BeginInvoke(() => ShowUnexpected(e.Exception));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);
            try
            {
                MessageBox.Show("予期しないエラーが発生しました。アプリを終了します。\n\nログ: " + AppLog.FilePath, "FolderDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // nothing more we can do
            }
        };
    }

    private void ShowUnexpected(Exception ex)
    {
        if (DateTime.UtcNow - _lastErrorDialog < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastErrorDialog = DateTime.UtcNow;
        string detail = (ex is AggregateException agg ? agg.Flatten().InnerException ?? ex : ex).Message;
        MessageBox.Show(
            MainWindow,
            $"予期しないエラーが発生しました。\n\n詳細: {detail}\n\nログ: {AppLog.FilePath}",
            "FolderDeck",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
