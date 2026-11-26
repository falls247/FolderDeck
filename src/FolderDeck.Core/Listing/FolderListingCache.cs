using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Listing;

/// <summary>
/// Folder listings cached per path with a TTL (60 s by default, §7.4). Concurrent requests for the
/// same path share one enumeration; the enumeration is cancelled once every caller has gone.
/// Bounded by the number of listings and by their entries in total (<see cref="DefaultMaxTotalEntries"/>);
/// expired listings (never served again) are dropped at most once per TTL, when a listing is stored.
/// </summary>
public sealed class FolderListingCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>The entries of every cached listing together (each counted up to a quarter of it), by default.</summary>
    public const int DefaultMaxTotalEntries = 200_000;

    private readonly IFileSystem _fs;
    private readonly Func<bool> _includeHidden;
    private readonly Func<long> _ticks;
    private readonly long _ttlMs;
    private readonly int _maxEntries;
    private readonly long _maxTotalEntries;
    private readonly VolumeThrottle _throttle;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private long _totalWeight;  // Σ Weight(listing) over _cache: what the entry budget counts
    private long _totalEntries; // Σ Entries.Count over _cache (diagnostics)
    private long _lastSweep;    // when expired listings were last dropped

    public FolderListingCache(
        IFileSystem fs,
        Func<bool> includeHidden,
        TimeSpan? ttl = null,
        int maxEntries = 1500,
        VolumeThrottle? throttle = null,
        Func<long>? tickSource = null,
        int maxTotalEntries = DefaultMaxTotalEntries)
    {
        _fs = fs;
        _includeHidden = includeHidden;
        _ttlMs = (long)(ttl ?? DefaultTtl).TotalMilliseconds;
        _maxEntries = Math.Max(16, maxEntries);
        _throttle = throttle ?? new VolumeThrottle();
        _ticks = tickSource ?? (() => Environment.TickCount64);
        _maxTotalEntries = Math.Max(4, maxTotalEntries);
        _lastSweep = _ticks();
    }

    public IFileSystem FileSystem => _fs;

    public bool IncludeHidden => _includeHidden();

    /// <summary>How many listings are cached (diagnostics, tests).</summary>
    public int CachedListings
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>The entries of the cached listings together (diagnostics, tests).</summary>
    public long CachedEntries
    {
        get
        {
            lock (_gate)
            {
                return _totalEntries;
            }
        }
    }

    /// <summary>Returns a cached, still-fresh listing without starting any I/O.</summary>
    public bool TryGetFresh(string path, out FolderListing listing)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var entry) && IsFresh(entry))
            {
                listing = entry.Listing;
                return true;
            }
        }

        listing = null!;
        return false;
    }

    public Task<FolderListing> GetAsync(string path, CancellationToken cancellationToken)
    {
        InFlight flight;
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var entry))
            {
                if (IsFresh(entry))
                {
                    return Task.FromResult(entry.Listing);
                }

                Drop(path);
            }

            bool hidden = _includeHidden();
            if (!_inFlight.TryGetValue(path, out flight!) || flight.IncludeHidden != hidden)
            {
                flight = new InFlight(path, hidden, _generation);
                _inFlight[path] = flight;
                flight.Task = Task.Run(() => RunAsync(flight), CancellationToken.None);
                _ = flight.Task.ContinueWith(t => Complete(flight, t), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            flight.Waiters++;
        }

        return WaitAsync(flight, cancellationToken);
    }

    /// <summary>
    /// Drops every cached listing (F5, a file operation, or the hidden-files setting changed). A
    /// listing still running was started before: it finishes for those already waiting, but a
    /// later <see cref="GetAsync"/> starts a new one instead of joining it, and it is not cached.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _totalWeight = 0;
            _totalEntries = 0;
            _generation++;
            foreach (var flight in _inFlight.Values)
            {
                flight.Detached = true;
            }

            _inFlight.Clear();
        }
    }

    /// <summary>Drops the listing of <paramref name="path"/>, cached or still running (as <see cref="Clear"/> does for every path).</summary>
    public void Invalidate(string path)
    {
        lock (_gate)
        {
            Drop(path);
            if (_inFlight.Remove(path, out var flight))
            {
                flight.Detached = true;
            }
        }
    }

    private async Task<FolderListing> RunAsync(InFlight flight)
    {
        var token = flight.Cts.Token;
        using (await _throttle.EnterAsync(flight.Path, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            return FolderLister.List(_fs, flight.Path, flight.IncludeHidden, token);
        }
    }

    private async Task<FolderListing> WaitAsync(InFlight flight, CancellationToken cancellationToken)
    {
        try
        {
            return await flight.Task!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                flight.Waiters--;
                if (flight.Waiters <= 0 && !flight.Task!.IsCompleted)
                {
                    flight.Cts.Cancel();
                    if (_inFlight.TryGetValue(flight.Path, out var current) && ReferenceEquals(current, flight))
                    {
                        _inFlight.Remove(flight.Path);
                    }
                }
            }
        }
    }

    private void Complete(InFlight flight, Task<FolderListing> task)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(flight.Path, out var current) && ReferenceEquals(current, flight))
            {
                _inFlight.Remove(flight.Path);
            }

            if (task.Status == TaskStatus.RanToCompletion
                && !flight.Detached
                && flight.Generation == _generation
                && flight.IncludeHidden == _includeHidden())
            {
                long now = _ticks();
                Put(flight.Path, task.Result, now);
                if (_cache.Count > _maxEntries || _totalWeight > _maxTotalEntries || now - _lastSweep > _ttlMs)
                {
                    Trim(now, keep: flight.Path);
                }
            }
        }

        flight.Cts.Dispose();
    }

    /// <summary>
    /// What a listing counts for in the entry budget: one huge folder (shown by a tab, which holds its
    /// listing anyway) counts as a quarter of the budget at most, so it does not make every later
    /// listing push all the others out.
    /// </summary>
    private long Weight(FolderListing listing) => Math.Min(listing.Entries.Count, _maxTotalEntries / 4) + 1;

    /// <summary>Every write to <see cref="_cache"/> goes through here or <see cref="Drop"/> (the totals follow it).</summary>
    private void Put(string path, FolderListing listing, long now)
    {
        Drop(path);
        _cache[path] = new CacheEntry(listing, now);
        _totalWeight += Weight(listing);
        _totalEntries += listing.Entries.Count;
    }

    private void Drop(string path)
    {
        if (_cache.Remove(path, out var old))
        {
            _totalWeight -= Weight(old.Listing);
            _totalEntries -= old.Listing.Entries.Count;
        }
    }

    /// <summary>
    /// Drops what is expired (callers only ever get fresh listings), then the oldest down to 9/10 of
    /// both limits — never <paramref name="keep"/>, the listing just stored.
    /// </summary>
    private void Trim(long now, string keep)
    {
        _lastSweep = now;
        foreach (string key in _cache.Where(kv => now - kv.Value.Ticks > _ttlMs).Select(kv => kv.Key).ToList())
        {
            Drop(key);
        }

        if (_cache.Count <= _maxEntries && _totalWeight <= _maxTotalEntries)
        {
            return;
        }

        int countTarget = _maxEntries * 9 / 10;
        long weightTarget = _maxTotalEntries * 9 / 10;
        foreach (var kv in _cache.OrderBy(kv => kv.Value.Ticks).ToList())
        {
            if (_cache.Count <= countTarget && _totalWeight <= weightTarget)
            {
                break;
            }

            if (!string.Equals(kv.Key, keep, StringComparison.OrdinalIgnoreCase))
            {
                Drop(kv.Key);
            }
        }
    }

    private bool IsFresh(CacheEntry entry) => _ticks() - entry.Ticks <= _ttlMs;

    private sealed record CacheEntry(FolderListing Listing, long Ticks);

    private sealed class InFlight(string path, bool includeHidden, long generation)
    {
        public string Path { get; } = path;

        public bool IncludeHidden { get; } = includeHidden;

        public long Generation { get; } = generation;

        public CancellationTokenSource Cts { get; } = new();

        public Task<FolderListing>? Task { get; set; }

        public int Waiters { get; set; }

        /// <summary>Dropped by <see cref="Clear"/> / <see cref="Invalidate"/>: no new waiter joins it and its result is not cached.</summary>
        public bool Detached { get; set; }
    }
}
