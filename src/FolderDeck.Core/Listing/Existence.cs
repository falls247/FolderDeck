using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Listing;

public enum ExistState
{
    Folder,
    File,
    Missing,

    /// <summary>The check did not finish in time (disconnected network drive and similar).</summary>
    Timeout,
}

public static class Existence
{
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
    public static async Task<List<(T Item, ExistState State)>> FilterAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> pathOf,
        int limit,
        Func<string, CancellationToken, Task<ExistState>> check,
        CancellationToken cancellationToken,
        int parallelism = 16)
    {
        var kept = new List<(T, ExistState)>();
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
                if (states[i] != ExistState.Missing)
                {
                    kept.Add((slice[i], states[i]));
                }
            }
        }

        return kept;
    }
}
