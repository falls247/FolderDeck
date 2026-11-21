using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Stores;

/// <summary>All data files under <c>%LOCALAPPDATA%\FolderDeck\</c> (§13.1).</summary>
public sealed class DataStores
{
    public DataStores(string dataDirectory, IClock clock)
    {
        DataDirectory = dataDirectory;
        Registered = new RegisteredFoldersStore(Path.Combine(dataDirectory, RegisteredFoldersStore.DefaultFileName), clock);
        History = new HistoryStore(Path.Combine(dataDirectory, HistoryStore.DefaultFileName), clock);
        Pins = new PinStore(Path.Combine(dataDirectory, PinStore.DefaultFileName), clock);
        Excluded = new ExcludedStore(Path.Combine(dataDirectory, ExcludedStore.DefaultFileName), clock);
        Settings = new SettingsStore(Path.Combine(dataDirectory, SettingsStore.DefaultFileName), clock);
    }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderDeck");

    public string DataDirectory { get; }

    public RegisteredFoldersStore Registered { get; }

    public HistoryStore History { get; }

    public PinStore Pins { get; }

    public ExcludedStore Excluded { get; }

    public SettingsStore Settings { get; }

    public IEnumerable<StoreBase> All => [Registered, History, Pins, Excluded, Settings];

    /// <summary>Loads every store (blocking I/O).</summary>
    public void LoadAll()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not create data folder {DataDirectory}", ex);
        }

        foreach (var store in All)
        {
            store.Load();
        }
    }

    public void FlushAll()
    {
        foreach (var store in All)
        {
            try
            {
                store.Flush();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not save {store.FilePath}", ex);
            }
        }
    }
}
