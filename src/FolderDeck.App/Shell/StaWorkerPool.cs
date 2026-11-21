using System.Collections.Concurrent;
using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Shell;

/// <summary>
/// Dedicated STA worker threads for shell/COM calls (thumbnail extractors may assume STA).
/// A call that exceeds its timeout is abandoned: the caller gets a <see cref="TimeoutException"/>
/// and a fresh worker replaces the stuck one (R4).
/// </summary>
public sealed class StaWorkerPool : IDisposable
{
    private const int MaxThreads = 16;

    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly string _name;
    private readonly int _size;
    private readonly object _gate = new();
    private int _alive;
    private int _serial;
    private bool _disposed;

    public StaWorkerPool(int size, string name)
    {
        _size = Math.Max(1, size);
        _name = name;
        for (int i = 0; i < _size; i++)
        {
            StartWorker();
        }
    }

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var item = new WorkItem(() => work(), cancellationToken);
        _queue.Add(item, CancellationToken.None);
        return AwaitAsync<T>(item, cancellationToken, timeout);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _queue.CompleteAdding();
    }

    private async Task<T> AwaitAsync<T>(WorkItem item, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        using var reg = cancellationToken.Register(static s => ((WorkItem)s!).CancelIfNotStarted(), item);
        try
        {
            object? result = timeout is null
                ? await item.Completion.Task.ConfigureAwait(false)
                : await item.Completion.Task.WaitAsync(timeout.Value).ConfigureAwait(false);
            return (T)result!;
        }
        catch (TimeoutException)
        {
            var worker = item.Worker;
            if (worker is not null && !item.Completion.Task.IsCompleted)
            {
                worker.Abandoned = true;
                AppLog.Warn($"{_name}: a shell call did not return within {timeout}; worker abandoned.");
                StartWorker();
            }

            throw;
        }
    }

    private void StartWorker()
    {
        lock (_gate)
        {
            if (_disposed || _alive >= MaxThreads)
            {
                return;
            }

            _alive++;
            var worker = new Worker();
            var thread = new Thread(() => Loop(worker))
            {
                IsBackground = true,
                Name = $"{_name}-{Interlocked.Increment(ref _serial)}",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    private void Loop(Worker worker)
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (!item.TryStart(worker))
                {
                    continue;
                }

                try
                {
                    item.Completion.TrySetResult(item.Work());
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }

                if (worker.Abandoned)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _alive--;
            }
        }
    }

    private sealed class Worker
    {
        public volatile bool Abandoned;
    }

    private sealed class WorkItem(Func<object?> work, CancellationToken token)
    {
        private int _state; // 0 queued, 1 started, 2 cancelled

        public Func<object?> Work { get; } = work;

        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Worker? Worker { get; private set; }

        public bool TryStart(Worker worker)
        {
            if (token.IsCancellationRequested)
            {
                CancelIfNotStarted();
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return false;
            }

            Worker = worker;
            return true;
        }

        public void CancelIfNotStarted()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                Completion.TrySetCanceled(token);
            }
        }
    }
}
