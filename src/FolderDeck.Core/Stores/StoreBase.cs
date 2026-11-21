using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Stores;

/// <summary>
/// Common load/save plumbing. Mutations happen in memory under <see cref="Gate"/>; saving runs on a
/// worker thread, coalescing bursts of changes (<see cref="RequestSave"/>).
/// </summary>
public abstract class StoreBase
{
    private readonly object _saveGate = new();
    private readonly object _writeGate = new();
    private bool _dirty;
    private bool _saving;
    private Task _saveTask = Task.CompletedTask;

    protected StoreBase(string filePath, IClock clock)
    {
        FilePath = filePath;
        Clock = clock;
    }

    /// <summary>Raised (on a worker thread) when writing the file failed.</summary>
    public event Action<StoreBase, Exception>? SaveFailed;

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public LoadStatus LoadStatus { get; protected set; } = LoadStatus.Missing;

    public string? BackupPath { get; protected set; }

    public string? LoadDetail { get; protected set; }

    /// <summary>True when the file was broken and could not be moved aside: nothing is ever written.</summary>
    public bool IsDisabled => LoadStatus == LoadStatus.Failed;

    protected object Gate { get; } = new();

    protected IClock Clock { get; }

    public abstract void Load();

    /// <summary>Schedules a background save of the current state.</summary>
    public void RequestSave()
    {
        if (IsDisabled)
        {
            return;
        }

        lock (_saveGate)
        {
            _dirty = true;
            if (_saving)
            {
                return;
            }

            _saving = true;
            _saveTask = Task.Run(SaveLoop);
        }
    }

    /// <summary>Writes synchronously, waiting for any background save first (used at exit and in tests).</summary>
    public void Flush()
    {
        Task pending;
        lock (_saveGate)
        {
            pending = _saveTask;
        }

        try
        {
            pending.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // reported through SaveFailed
        }

        bool dirty;
        lock (_saveGate)
        {
            dirty = _dirty || !pending.IsCompleted;
            _dirty = false;
        }

        if (dirty)
        {
            SaveNow();
        }
    }

    /// <summary>Writes synchronously now.</summary>
    public void SaveNow()
    {
        if (IsDisabled)
        {
            return;
        }

        byte[] bytes;
        lock (Gate)
        {
            bytes = Serialize();
        }

        lock (_writeGate)
        {
            JsonFile.WriteAtomic(FilePath, bytes);
        }
    }

    protected abstract byte[] Serialize();

    protected void ApplyLoad<T>(LoadResult<T> result)
    {
        LoadStatus = result.Status;
        BackupPath = result.BackupPath;
        LoadDetail = result.Detail;
    }

    private void SaveLoop()
    {
        while (true)
        {
            lock (_saveGate)
            {
                if (!_dirty)
                {
                    _saving = false;
                    return;
                }

                _dirty = false;
            }

            try
            {
                SaveNow();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not save {FilePath}", ex);
                SaveFailed?.Invoke(this, ex);
            }
        }
    }
}
