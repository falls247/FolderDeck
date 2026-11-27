using System.Text;
using System.Windows;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
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
    private SessionMonitor? _session;
    private UiWatchdog? _watchdog;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _background;
    private int _exitLogged;
    private DateTime _lastErrorDialog = DateTime.MinValue;

    public ThemeManager? Theme { get; private set; }

    /// <summary>The single-instance lock, taken by <see cref="Program.Main"/> before WPF starts.</summary>
    internal SingleInstance? Instance { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // §4.4: a second launch only brings the running window to the front (normally already
        // decided in Program.Main; this is the fallback for WPF's generated Main).
        _instance = Instance ?? SingleInstance.Acquire();
        if (!_instance.IsFirst)
        {
            _instance.SignalFirstInstance();
            Shutdown(0);
            return;
        }

        string? dataDirArg = ParseDataDir(e.Args);
        string dataDir = dataDirArg ?? DataStores.DefaultDataDirectory;

        // A test run with --data-dir never reads or writes the Run key (the copy in use keeps its registration).
        bool managesRunKey = dataDirArg is null;
        _background = LoginStartup.IsBackgroundLaunch(e.Args);
        string logDir = Path.Combine(dataDir, "logs");
        string version = typeof(App).Assembly.GetName().Version?.ToString() ?? "?";
        AppLog.Initialize(logDir);
        AppLog.Info($"lifecycle: start FolderDeck {version} (data: {dataDir}; {DescribeExe()})");
        HookUnhandledExceptions();
        _session = new SessionMonitor(logDir, version);
        _session.Start(Dispatcher);
        _watchdog = new UiWatchdog(Dispatcher); // a UI thread that stops answering is logged (a hang logs nothing by itself)
        _watchdog.Start();
        var startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        string Since() => $"{(DateTime.Now - startedAt).TotalMilliseconds:F0} ms";
        AppLog.Info($"startup: OnStartup at {Since()}");

        Theme = new ThemeManager(this);
        Theme.Apply();

        var stores = new DataStores(dataDir, SystemClock.Instance);
        await Task.Run(() =>
        {
            stores.LoadAll(); // JSON I/O off the UI thread (§16)
            if (managesRunKey)
            {
                SyncStartAtLogin(stores.Settings);
            }
        });
        _services = new AppServices(stores) { ManagesRunKey = managesRunKey };
        _session.DescribeMemory = _services.DescribeMemory;
        AppLog.Info($"startup: data loaded at {Since()}");

        // The theme chosen in the settings (until now: Windows' mode) and 文字の大きさ, before any window exists.
        Theme.Apply(AppThemes.FromSetting(stores.Settings.Current.Theme));
        TextScale.Apply(stores.Settings.Current.TextScale);

        var vm = new MainViewModel(_services);
        var window = new MainWindow(vm, Theme);
        _window = window;
        window.RestorePlacement(stores.Settings.Current.Window);
        _session.Watch(window);
        _session.SessionEnded = EndSession;
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        SessionEnding += (_, _) => window.AllowClose(); // a hidden window must not refuse the sign-out
        AppLog.Info($"startup: window built at {Since()}");
        window.ContentRendered += (_, _) => AppLog.Info($"startup: first frame at {Since()}");

        // 常駐モード: the icon in the notification area, while resident (either setting or --background).
        _tray = new TrayIcon();
        _tray.ShowRequested += () => window.Reveal("tray");
        _tray.ExitRequested += () => window.RequestExit("the tray menu");
        vm.ResidentSettingsChanged += UpdateTray;
        UpdateTray();

        if (_background)
        {
            // No window yet: its handle only (the hooks, the session monitor and the tray need it). The first tab loads below.
            window.PrepareHidden();
            AppLog.Info($"resident: started in the background at {Since()}");
        }
        else
        {
            window.Show();
        }

        foreach (var store in stores.All)
        {
            store.SaveFailed += (s, ex) => Dispatcher.BeginInvoke(() => vm.Message.Error($"{s.FileName} を保存できませんでした：{ex.Message}"));
        }

        ReportLoadProblems(stores, vm.Message);
        _instance.Listen(() =>
        {
            long signalled = Environment.TickCount64;
            Dispatcher.BeginInvoke(() => window.Reveal("a second launch", signalled));
        });
        _instance.ListenExit(() => Dispatcher.BeginInvoke(() => window.RequestExit("another program (the exit event)")));
        await vm.InitializeAsync();
        if (_background)
        {
            AppLog.Info($"resident: first tab loaded in the background at {Since()}");
            window.PrewarmHidden();
        }
    }

    /// <summary>The icon shows while the app is resident: a setting of 常駐モード, or started with --background.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _services is null)
        {
            return;
        }

        var settings = _services.Stores.Settings.Current;
        _tray.SetVisible(_background || settings.KeepRunningOnClose || settings.StartAtLogin);
    }

    /// <summary>
    /// The registry is the truth for 「ログオン時に起動」: a value removed by hand, or left for an exe
    /// that was moved, turns the setting off (a value added by hand turns it on). Pool thread.
    /// </summary>
    private static void SyncStartAtLogin(SettingsStore settings)
    {
        var state = RunKeyRegistry.ReadState();
        bool registered = state == LoginStartupState.Registered;
        if (settings.Current.StartAtLogin != registered)
        {
            AppLog.Info($"resident: start_at_login follows the Run key ({state}): {registered}");
            settings.Update(s => s.StartAtLogin = registered);
        }
    }

    /// <summary>
    /// Windows ends the session (<c>WM_ENDSESSION</c>): the process may be ended as soon as the
    /// message returns, before WPF's shutdown runs <see cref="OnExit"/>. So the stores are saved
    /// and the exit is recorded right here (a hidden, resident window gets the message too).
    /// </summary>
    private void EndSession()
    {
        try
        {
            if (_window is { } window)
            {
                window.SaveForExit();
            }

            _services?.Stores.FlushAll();
        }
        finally
        {
            LogExit("session end");
            _session?.MarkCleanExit();
        }
    }

    private void LogExit(string how)
    {
        if (Interlocked.Exchange(ref _exitLogged, 1) == 0)
        {
            AppLog.Info($"lifecycle: exit ({how})");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Stores.FlushAll();
        }
        finally
        {
            LogExit($"code {e.ApplicationExitCode}");
            _tray?.Dispose();
            _services?.Dispose();
            Theme?.Dispose();
            _instance?.Dispose();
            _watchdog?.Dispose();
            _session?.MarkCleanExit();
        }

        base.OnExit(e);
    }

    /// <summary>Exe path and build time: a close right before a newer build points at a build script.</summary>
    private static string DescribeExe()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null)
            {
                return "exe: ?";
            }

            return $"exe: {exe}, built {File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "exe: ?";
        }
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
            AppLog.Error($"lifecycle: crash (unhandled exception, terminating={e.IsTerminating})", e.ExceptionObject as Exception);
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
