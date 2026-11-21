using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FolderDeck.Shell;

/// <summary>Follows Windows' app mode (light / dark) and swaps Themes/Light.xaml ↔ Dark.xaml (§15.1).</summary>
public sealed class ThemeManager : IDisposable
{
    private static readonly Uri LightUri = new("pack://application:,,,/FolderDeck;component/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri = new("pack://application:,,,/FolderDeck;component/Themes/Dark.xaml", UriKind.Absolute);

    private readonly Application _app;
    private ResourceDictionary? _current;

    public ThemeManager(Application app)
    {
        _app = app;
        // App.xaml starts with Light.xaml at index 0: that is the dictionary we swap.
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0 && merged[0].Source?.OriginalString.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase) == true)
        {
            _current = merged[0];
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event Action? ThemeChanged;

    public bool IsDark { get; private set; }

    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Apply() => Apply(ReadSystemIsDark(), force: true);

    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                ShellInterop.SetDarkTitleBar(hwnd, IsDark);
            }
            catch (Exception)
            {
                // optional (§15.1)
            }
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void Apply(bool dark, bool force)
    {
        if (!force && dark == IsDark && _current is not null)
        {
            return;
        }

        IsDark = dark;
        var dictionary = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var merged = _app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            int index = merged.IndexOf(_current);
            if (index >= 0)
            {
                merged[index] = dictionary;
            }
            else
            {
                merged.Insert(0, dictionary);
            }
        }
        else
        {
            merged.Insert(0, dictionary);
        }

        _current = dictionary;
        foreach (Window window in _app.Windows)
        {
            ApplyTitleBar(window);
        }

        ThemeChanged?.Invoke();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _app.Dispatcher.BeginInvoke(() => Apply(ReadSystemIsDark(), force: false));
        }
    }
}
