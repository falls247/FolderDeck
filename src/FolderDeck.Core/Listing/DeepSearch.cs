using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Listing;

/// <summary>How a recursive search ended.</summary>
public enum DeepSearchEnd
{
    /// <summary>Every subfolder that could be read was walked.</summary>
    Complete,

    /// <summary><see cref="DeepSearch.MaxResults"/> items were found; the rest is not looked for.</summary>
    Limit,
}

/// <summary>
/// The search of the shown folder's subfolders (setting <c>search_scope</c> = <c>recursive</c>):
/// the names that hold every word of the query (<see cref="NameSearch"/>) anywhere below the
/// folder, its direct children excepted (the list shows those itself). Two sources run at once and
/// are merged without duplicates: an index (Windows Search: <paramref name="index"/> of
/// <see cref="RunAsync"/>, which answers in a moment for what it holds) and a walk of the folders
/// (complete, also where nothing is indexed). An index hit is checked on the disk and against the
/// listing filter before it counts (the index may be behind). The walk does not follow junctions
/// or symbolic links (no loops) and skips folders it cannot read. Pool threads only.
/// </summary>
public static class DeepSearch
{
    public const int MaxResults = 5000;

    /// <summary>Found items are handed over in batches at most this often (the list is rebuilt for each).</summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Once this many were handed over, batches come only every <see cref="SlowBatchInterval"/> (a long list rebuilt often is a busy one).</summary>
    private const int SlowBatchAfter = 200;

    private static readonly TimeSpan SlowBatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs the search. <paramref name="index"/> (may be null) returns full paths below the root
    /// whose names may match; <paramref name="onFound"/> gets the new items, in batches, from a pool
    /// thread. Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public static async Task<DeepSearchEnd> RunAsync(
        IFileSystem fs,
        string root,
        string query,
        ListingFilter filter,
        Func<string, IReadOnlyList<string>, CancellationToken, IReadOnlyList<string>>? index,
        Action<IReadOnlyList<FsEntry>> onFound,
        CancellationToken cancellationToken)
    {
        var terms = NameSearch.Terms(query);
        if (terms.Length == 0)
        {
            return DeepSearchEnd.Complete;
        }

        var sink = new Sink(root, onFound);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stop.Token;
        var indexTask = index is null
            ? Task.CompletedTask
            : Task.Run(() => FromIndex(fs, root, terms, filter, index, sink, token), token);
        var walkTask = Task.Run(() => Walk(fs, root, terms, filter, sink, token), token);
        try
        {
            await Task.WhenAll(IgnoreIndexFailure(indexTask), walkTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sink.IsFull && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            stop.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();
        sink.Flush();
        return sink.IsFull ? DeepSearchEnd.Limit : DeepSearchEnd.Complete;

        async Task IgnoreIndexFailure(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn("Deep search: the index could not be asked; the walk goes on alone", ex);
            }
        }

        void Full()
        {
            if (sink.IsFull)
            {
                stop.Cancel();
            }
        }

        void Walk(IFileSystem fileSystem, string start, string[] words, ListingFilter listing, Sink found, CancellationToken ct)
        {
            var pending = new Queue<string>();
            foreach (var entry in Children(fileSystem, start, listing, ct))
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    pending.Enqueue(entry.FullPath);
                }
            }

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var entry in Children(fileSystem, pending.Dequeue(), listing, ct))
                {
                    if (NameSearch.Matches(entry.Name, words))
                    {
                        found.Add(entry);
                        Full();
                    }

                    if (entry.IsDirectory && !entry.IsReparsePoint)
                    {
                        pending.Enqueue(entry.FullPath);
                    }
                }
            }
        }

        void FromIndex(IFileSystem fileSystem, string start, string[] words, ListingFilter listing, Func<string, IReadOnlyList<string>, CancellationToken, IReadOnlyList<string>> ask, Sink found, CancellationToken ct)
        {
            foreach (string path in ask(start, words, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsDeepBelow(start, path) || !NameSearch.Matches(PathTools.DisplayName(path), words) || !PassesNames(start, path, listing))
                {
                    continue;
                }

                if (fileSystem.GetEntry(path) is { } entry && Passes(entry, listing))
                {
                    found.Add(entry);
                    Full();
                }
            }
        }
    }

    /// <summary>
    /// The Windows Search query (its SQL, for the provider <c>Search.CollatorDSO</c>) for the names
    /// below <paramref name="root"/> that hold every one of <paramref name="terms"/>: one
    /// <c>LIKE '%term%'</c> per word (no case), at most <paramref name="max"/> rows. Quotes are
    /// doubled and the LIKE wildcards of a word taken literally (<c>[%]</c>, <c>[_]</c>, <c>[[]</c>).
    /// </summary>
    public static string IndexQuery(string root, IReadOnlyList<string> terms, int max)
    {
        static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);
        static string Literal(string term) =>
            Quote(term).Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);

        string scope = Quote(root.TrimEnd('\\') + "\\");
        var where = terms.Select(t => $" AND System.FileName LIKE '%{Literal(t)}%'");
        return $"SELECT TOP {max} System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:{scope}'{string.Concat(where)}";
    }

    /// <summary>Below the root's direct children (those are the list's own).</summary>
    public static bool IsDeepBelow(string root, string path)
    {
        string parent = PathTools.ParentOf(path);
        return parent.Length > 0 && !PathTools.PathEquals(parent, root) && PathTools.IsSameOrUnder(path, root);
    }

    private static IReadOnlyList<FsEntry> Children(IFileSystem fs, string folder, ListingFilter filter, CancellationToken ct)
    {
        try
        {
            return fs.EnumerateDirectory(folder, filter, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return []; // a folder that cannot be read is skipped, as Explorer's search does
        }
    }

    /// <summary>The listing filter for one entry (what <see cref="IFileSystem.EnumerateDirectory"/> skips).</summary>
    private static bool Passes(in FsEntry entry, ListingFilter filter) =>
        ((filter & ListingFilter.Hidden) != 0 || (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
        && ((filter & ListingFilter.DotNames) != 0 || !entry.Name.StartsWith('.'));

    /// <summary>No folder between the root and the item starts with a dot unless dot names are shown (the walk would not enter it).</summary>
    private static bool PassesNames(string root, string path, ListingFilter filter)
    {
        if ((filter & ListingFilter.DotNames) != 0)
        {
            return true;
        }

        string rel = Path.GetRelativePath(root, path);
        return !rel.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'));
    }

    /// <summary>Collects the found items without duplicates and hands them over in batches.</summary>
    private sealed class Sink(string root, Action<IReadOnlyList<FsEntry>> onFound)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _seen = new(PathTools.Comparer);
        private List<FsEntry> _batch = [];
        private long _lastFlush = Environment.TickCount64;

        public bool IsFull
        {
            get
            {
                lock (_gate)
                {
                    return _seen.Count >= MaxResults;
                }
            }
        }

        public void Add(in FsEntry entry)
        {
            List<FsEntry>? ready = null;
            lock (_gate)
            {
                if (_seen.Count >= MaxResults || !IsDeepBelow(root, entry.FullPath) || !_seen.Add(entry.FullPath))
                {
                    return;
                }

                _batch.Add(entry);
                var interval = _seen.Count - _batch.Count >= SlowBatchAfter ? SlowBatchInterval : BatchInterval;
                if (Environment.TickCount64 - _lastFlush >= interval.TotalMilliseconds)
                {
                    ready = TakeBatch();
                }
            }

            if (ready is not null)
            {
                onFound(ready);
            }
        }

        public void Flush()
        {
            List<FsEntry>? ready;
            lock (_gate)
            {
                ready = _batch.Count > 0 ? TakeBatch() : null;
            }

            if (ready is not null)
            {
                onFound(ready);
            }
        }

        private List<FsEntry> TakeBatch()
        {
            var ready = _batch;
            _batch = [];
            _lastFlush = Environment.TickCount64;
            return ready;
        }
    }
}
