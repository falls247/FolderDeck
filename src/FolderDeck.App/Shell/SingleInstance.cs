using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Shell;

/// <summary>
/// Named mutex (<c>Local\FolderDeck.SingleInstance</c>) plus an event the second instance signals
/// to bring the first one to the front (§4.4).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FolderDeck.SingleInstance";
    private const string EventName = @"Local\FolderDeck.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    private readonly bool _owned;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle ev, bool owned)
    {
        _mutex = mutex;
        _event = ev;
        _owned = owned;
    }

    public bool IsFirst => _owned;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return new SingleInstance(mutex, ev, created);
    }

    /// <summary>Second instance: let the first one take the foreground and wake it up.</summary>
    public void SignalFirstInstance()
    {
        try
        {
            ShellInterop.AllowAnyForeground();
            _event.Set();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not signal the running instance", ex);
        }
    }

    /// <summary>First instance: <paramref name="onActivate"/> runs (on a pool thread) whenever a second instance starts.</summary>
    public void Listen(Action onActivate)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _event.Dispose();
    }
}
