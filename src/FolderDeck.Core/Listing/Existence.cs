using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Ordering;

namespace FolderDeck.Core.Listing;

public enum ExistState
{
    Folder,
    File,
    Missing,

    /// <summary>The check did not finish in time (disconnected network drive and similar).</summary>
    Timeout,
}

/// <summary>
/// Result of <see cref="Existence.ProbeAsync"/>: what the path is and, when known, its last write
/// time (UTC) and, for a file, its size in bytes (null for a folder and whenever it is not known).
/// </summary>
public readonly record struct ExistProbe(ExistState State, DateTime? LastWriteTimeUtc, long? Length = null);

public static class Existence
{
    /// <summary>
    /// Like <see cref="CheckAsync"/> (same single worker call, same timeout) but also returns the
    /// last write time and a file's size, used by the 更新日 and サイズ sort modes and the details list.
    /// </summary>
    public static async Task<ExistProbe> ProbeAsync(IFileSystem fs, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var work = Task.Run(() => Probe(fs, path), CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ExistProbe(ExistState.Timeout, null);
        }
    }

    /// <summary>Checks a path on a worker thread and gives up after <paramref name="timeout"/>.</summary>
    public static async Task<ExistState> CheckAsync(IFileSystem fs, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var work = Task.Run(() => fs.GetKind(path), CancellationToken.None);
        try
        {
            var kind = await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return kind switch
            {
                PathKind.Folder => ExistState.Folder,
                PathKind.File => ExistState.File,
                _ => ExistState.Missing,
            };
        }
        catch (TimeoutException)
        {
            return ExistState.Timeout;
        }
    }

    /// <summary>
    /// Walks <paramref name="items"/> in order and keeps the ones that exist (or timed out) until
    /// <paramref name="limit"/> are kept. Checks run <paramref name="parallelism"/> at a time.
    /// </summary>
    public static Task<List<(T Item, ExistState State)>> FilterAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> pathOf,
        int limit,
        Func<string, CancellationToken, Task<ExistState>> check,
        CancellationToken cancellationToken,
        int parallelism = 16) =>
        FilterCoreAsync(items, pathOf, limit, check, s => s != ExistState.Missing, cancellationToken, parallelism);

    /// <summary><see cref="FilterAsync"/> with <see cref="ProbeAsync"/>-style checks: each kept item keeps its probe.</summary>
    public static Task<List<(T Item, ExistProbe Probe)>> FilterProbedAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> pathOf,
        int limit,
        Func<string, CancellationToken, Task<ExistProbe>> probe,
        CancellationToken cancellationToken,
        int parallelism = 16) =>
        FilterCoreAsync(items, pathOf, limit, probe, p => p.State != ExistState.Missing, cancellationToken, parallelism);

    private static ExistProbe Probe(IFileSystem fs, string path)
    {
        if (fs.GetEntry(path) is { } entry)
        {
            return entry.IsDirectory
                ? new ExistProbe(ExistState.Folder, CardOrdering.KnownTime(entry.LastWriteTimeUtc))
                : new ExistProbe(ExistState.File, CardOrdering.KnownTime(entry.LastWriteTimeUtc), entry.Length);
        }

        // GetEntry can miss what GetKind still sees (no access to the parent): keep CheckAsync's answer.
        var state = fs.GetKind(path) switch
        {
            PathKind.Folder => ExistState.Folder,
            PathKind.File => ExistState.File,
            _ => ExistState.Missing,
        };
        return new ExistProbe(state, null);
    }

    private static async Task<List<(T Item, TState State)>> FilterCoreAsync<T, TState>(
        IReadOnlyList<T> items,
        Func<T, string> pathOf,
        int limit,
        Func<string, CancellationToken, Task<TState>> check,
        Func<TState, bool> keep,
        CancellationToken cancellationToken,
        int parallelism)
    {
        var kept = new List<(T, TState)>();
        int index = 0;
        while (index < items.Count && kept.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batch = Math.Min(Math.Max(parallelism, limit - kept.Count), items.Count - index);
            batch = Math.Min(batch, Math.Max(parallelism, 1) * 4);
            var slice = new T[batch];
            for (int i = 0; i < batch; i++)
            {
                slice[i] = items[index + i];
            }

            index += batch;
            using var gate = new SemaphoreSlim(Math.Max(1, parallelism));
            var tasks = slice.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await check(pathOf(item), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            var states = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (int i = 0; i < slice.Length && kept.Count < limit; i++)
            {
                if (keep(states[i]))
                {
                    kept.Add((slice[i], states[i]));
                }
            }
        }

        return kept;
    }
}
