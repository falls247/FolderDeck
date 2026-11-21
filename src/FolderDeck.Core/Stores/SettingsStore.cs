using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;

namespace FolderDeck.Core.Stores;

public sealed class WindowPlacementSetting
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool Maximized { get; set; }
}

public sealed class LocationSetting
{
    public string Root { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

/// <summary>settings.json (§13.2). Missing keys keep these defaults.</summary>
public sealed class AppSettings
{
    public const int CardSizeMin = 120;
    public const int CardSizeMax = 400;
    public const int CardSizeStep = 20;
    public const int CardSizeDefault = 200;

    public const int ThumbCacheMbDefault = 512;
    public const int ThumbCacheMbMax = 8192;

    public const string TabRegistered = "registered";
    public const string TabRecent = "recent";
    public const string TabFrequent = "frequent";

    public int Version { get; set; } = 1;

    public WindowPlacementSetting? Window { get; set; } = new();

    public string? LastTab { get; set; } = TabRegistered;

    public Dictionary<string, LocationSetting>? Locations { get; set; } = new();

    public int CardSize { get; set; } = CardSizeDefault;

    /// <summary>Card shape for every tab: <c>portrait</c> (3:4 thumbnail) or <c>square</c> (1:1).</summary>
    public string? CardShape { get; set; } = CardShapes.Portrait;

    /// <summary>
    /// Sort mode per tab: <see cref="TabRegistered"/> / <see cref="TabRecent"/> / <see cref="TabFrequent"/> →
    /// <c>manual</c>, <c>name_asc</c>, <c>name_desc</c>, <c>modified_asc</c>, <c>modified_desc</c>.
    /// </summary>
    public Dictionary<string, string>? SortModes { get; set; } = new();

    public bool ShowHidden { get; set; }

    public bool ConfirmExecutables { get; set; } = true;

    public bool IncludeWindowsRecent { get; set; } = true;

    public int RecentLimit { get; set; } = 100;

    public int FrequentLimit { get; set; } = 50;

    public DateTimeOffset? RecentClearedAt { get; set; }

    public bool AddToWindowsRecent { get; set; }

    /// <summary>"Show in Explorer" uses a new tab of the running File Explorer window (Windows 11 22H2+).</summary>
    public bool OpenExplorerInNewTab { get; set; }

    /// <summary>
    /// Upper limit, in megabytes, of the thumbnail cache kept in the data folder (<c>thumbs</c>);
    /// 0 turns the disk cache off. Read at startup.
    /// </summary>
    public int ThumbCacheMb { get; set; } = ThumbCacheMbDefault;

    public static int ClampCardSize(int value)
    {
        int clamped = Math.Clamp(value, CardSizeMin, CardSizeMax);
        int steps = (int)Math.Round((clamped - CardSizeMin) / (double)CardSizeStep);
        return CardSizeMin + (steps * CardSizeStep);
    }

    /// <summary>Sort mode of a tab; missing or unknown values are <see cref="CardSortMode.Manual"/>.</summary>
    public CardSortMode SortModeOf(string tab) =>
        SortModes is not null && SortModes.TryGetValue(tab, out string? mode) ? CardSortModes.FromSetting(mode) : CardSortMode.Manual;

    public void SetSortMode(string tab, CardSortMode mode)
    {
        SortModes ??= new Dictionary<string, string>();
        SortModes[tab] = CardSortModes.ToSetting(mode);
    }

    /// <summary>Fills gaps and pulls out-of-range values back into range.</summary>
    public void Normalize()
    {
        Version = 1;
        Window ??= new WindowPlacementSetting();
        if (Window.Width < 200 || double.IsNaN(Window.Width))
        {
            Window.Width = 1280;
        }

        if (Window.Height < 150 || double.IsNaN(Window.Height))
        {
            Window.Height = 800;
        }

        if (LastTab is not (TabRegistered or TabRecent or TabFrequent))
        {
            LastTab = TabRegistered;
        }

        Locations ??= new Dictionary<string, LocationSetting>();
        foreach (string tab in new[] { TabRegistered, TabRecent, TabFrequent })
        {
            if (!Locations.TryGetValue(tab, out var loc) || loc is null)
            {
                Locations[tab] = new LocationSetting();
            }
            else
            {
                loc.Root ??= string.Empty;
                loc.Path ??= string.Empty;
            }
        }

        SortModes ??= new Dictionary<string, string>();
        foreach (string tab in new[] { TabRegistered, TabRecent, TabFrequent })
        {
            SortModes.TryGetValue(tab, out string? mode);
            SortModes[tab] = CardSortModes.ToSetting(CardSortModes.FromSetting(mode));
        }

        CardSize = ClampCardSize(CardSize);
        CardShape = CardShapes.ToSetting(CardShapes.FromSetting(CardShape));
        RecentLimit = Math.Clamp(RecentLimit, 10, 500);
        FrequentLimit = Math.Clamp(FrequentLimit, 10, 200);
        ThumbCacheMb = Math.Clamp(ThumbCacheMb, 0, ThumbCacheMbMax);
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Window = Window is null ? null : new WindowPlacementSetting
        {
            Left = Window.Left,
            Top = Window.Top,
            Width = Window.Width,
            Height = Window.Height,
            Maximized = Window.Maximized,
        };
        copy.Locations = Locations?.ToDictionary(kv => kv.Key, kv => new LocationSetting { Root = kv.Value.Root, Path = kv.Value.Path });
        copy.SortModes = SortModes is null ? null : new Dictionary<string, string>(SortModes);
        return copy;
    }
}

public sealed class SettingsStore : StoreBase
{
    public const string DefaultFileName = "settings.json";

    private AppSettings _settings = new();

    public SettingsStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
        _settings.Normalize();
    }

    /// <summary>A copy of the current settings.</summary>
    public AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _settings.Clone();
            }
        }
    }

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new AppSettings(), null, Clock);
        ApplyLoad(result);
        var loaded = result.Data;
        loaded.Normalize();
        lock (Gate)
        {
            _settings = loaded;
        }
    }

    /// <summary>Applies <paramref name="change"/> to the stored settings and schedules a save.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (Gate)
        {
            change(_settings);
            _settings.Normalize();
        }

        RequestSave();
    }

    protected override byte[] Serialize() => JsonFile.Serialize(_settings);
}
