using System.Collections.Concurrent;
using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Shell;

/// <summary>
/// A call of a <see cref="StaWorkerPool"/> that no worker took up within the queue timeout (every
/// worker is stuck and no more may be created). Unlike the call timeout, nothing ran: trying the same
/// pool again (a fallback icon) waits as long again.
/// </summary>
public sealed class QueueTimeoutException(string message) : TimeoutException(message);

/// <summary>
/// Dedicated STA worker threads for shell/COM calls (thumbnail extractors may assume STA).
/// A call that exceeds its timeout is abandoned: the caller gets a <see cref="TimeoutException"/>
/// and a fresh worker replaces the stuck one (R4). The timeout covers the call itself, not the
/// wait in the queue — a long queue (a folder full of pictures) is not a stuck shell.
/// </summary>
public sealed class StaWorkerPool : IDisposable
{
    private const int MaxThreads = 16;

    /// <summary>
    /// A call that has not even started after this long gives up too: every worker is stuck and
    /// no more may be created, so the caller should show its fallback rather than wait for ever.
    /// </summary>
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(60);

    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly string _name;
    private readonly int _size;
    private readonly ThreadPriority _priority;
    private readonly object _gate = new();
    private int _alive;
    private int _working; // alive and not abandoned
    private int _serial;
    private bool _disposed;
    private bool _limitLogged;
    private int _queueTimeoutsLogged;

    public StaWorkerPool(int size, string name, ThreadPriority priority = ThreadPriority.Normal)
    {
        _size = Math.Max(1, size);
        _name = name;
        _priority = priority;
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
            if (timeout is null)
            {
                return (T)(await item.Completion.Task.ConfigureAwait(false))!;
            }

            // Wait for a worker to pick the call up (or for its cancellation), then time the call.
            try
            {
                await item.Started.Task.WaitAsync(QueueTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException) when (item.CancelIfNotStarted())
            {
                // Still queued (now cancelled, so it does not run for nobody). A worker that took it
                // just now wins: the call is timed below like any other.
                if (Interlocked.Increment(ref _queueTimeoutsLogged) == 1)
                {
                    AppLog.Warn($"{_name}: calls waited {QueueTimeout.TotalSeconds:F0} s for a worker (all stuck); such calls give up without a warning each.");
                }

                throw new QueueTimeoutException($"{_name}: no worker took the call within {QueueTimeout}.");
            }

            object? result = await item.Completion.Task.WaitAsync(timeout.Value).ConfigureAwait(false);
            return (T)result!;
        }
        catch (TimeoutException ex) when (ex is not QueueTimeoutException)
        {
            var worker = item.Worker;
            if (worker is not null && !item.Completion.Task.IsCompleted)
            {
                AppLog.Warn($"{_name}: a call did not return within {timeout}; worker abandoned.");
                Abandon(worker);
            }

            throw;
        }
    }

    private void Abandon(Worker worker)
    {
        lock (_gate)
        {
            if (worker.Abandoned)
            {
                return;
            }

            worker.Abandoned = true;
            _working--;
        }

        StartWorker();
    }

    private void StartWorker()
    {
        bool logLimit;
        int working;
        lock (_gate)
        {
            // Once per pool: stuck calls hold every thread, so no replacement until one returns.
            logLimit = !_disposed && _alive >= MaxThreads && !_limitLogged;
            _limitLogged |= logLimit;
            working = _working;
            if (!_disposed && _alive < MaxThreads)
            {
                _alive++;
                _working++;
                var worker = new Worker();
                var thread = new Thread(() => Loop(worker))
                {
                    IsBackground = true,
                    Name = $"{_name}-{Interlocked.Increment(ref _serial)}",
                    Priority = _priority,
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }

        if (logLimit)
        {
            AppLog.Warn($"{_name}: thread limit ({MaxThreads}) reached by stuck calls; {working} of {_size} workers left.");
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
            bool refill;
            lock (_gate)
            {
                _alive--;
                if (!worker.Abandoned)
                {
                    _working--;
                }

                // An abandoned worker that could not be replaced (thread limit) is replaced now.
                refill = !_disposed && !_queue.IsAddingCompleted && _working < _size;
            }

            if (refill)
            {
                StartWorker();
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

        /// <summary>Completes when a worker takes the call, or when it is cancelled while queued.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            Started.TrySetResult();
            return true;
        }

        /// <summary>Cancels the call if no worker has taken it; false when one has (or it was cancelled before).</summary>
        public bool CancelIfNotStarted()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                Completion.TrySetCanceled(token);
                Started.TrySetResult();
                return true;
            }

            return false;
        }
    }
}
