using System.Collections.Concurrent;

namespace FolderDeck.Core.Listing;

/// <summary>
/// Limits concurrent blocking I/O per volume, so a hung network share cannot starve local drives.
/// </summary>
public sealed class VolumeThrottle
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _perVolume;

    public VolumeThrottle(int perVolume = 4)
    {
        _perVolume = Math.Max(1, perVolume);
    }

    public async Task<IDisposable> EnterAsync(string path, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(VolumeKey(path), _ => new SemaphoreSlim(_perVolume, _perVolume));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    internal static string VolumeKey(string path)
    {
        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
