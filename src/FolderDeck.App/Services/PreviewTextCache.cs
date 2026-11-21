using FolderDeck.Core.Listing;
using FolderDeck.Shell;

namespace FolderDeck.Services;

/// <summary>
/// Text excerpts for the hover preview — Word paragraphs, sheet cells, text-file lines — read on a
/// background thread and kept in a small LRU (keyed by path, modification time and length) so
/// resting on the same row again shows them at once. Failures are not cached.
/// </summary>
public sealed class PreviewTextCache
{
    private const int Capacity = 32;

    /// <summary>Like a shell thumbnail call: a read that takes longer is given up.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly LruCache<string, object> _cache = new(Capacity, 16L * 1024 * 1024, EstimateBytes);

    /// <summary>
    /// A <see cref="WordExcerpt"/>, <see cref="SheetExcerpt"/> or <see cref="TextExcerpt"/> for
    /// <paramref name="kind"/>, or null when the file cannot be read (or is binary).
    /// </summary>
    public async Task<object?> GetAsync(string path, PreviewKind kind, DateTime modifiedUtc, long length, CancellationToken cancellationToken)
    {
        string key = $"{path.ToUpperInvariant()}|{modifiedUtc.Ticks}|{length}|{(int)kind}";
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        object? excerpt;
        try
        {
            excerpt = await Task.Run<object?>(
                () => kind switch
                {
                    PreviewKind.WordDocument => OfficeText.ReadWord(path, cancellationToken: cancellationToken),
                    PreviewKind.Spreadsheet => OfficeText.ReadSheet(path, cancellationToken: cancellationToken),
                    PreviewKind.Text => TextPreview.Read(path, cancellationToken: cancellationToken),
                    _ => null,
                },
                cancellationToken).WaitAsync(ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null; // a stalled drive: the read finishes (or not) in the background, unused
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (excerpt is not null)
        {
            _cache.Set(key, excerpt);
        }

        return excerpt;
    }

    private static long EstimateBytes(object excerpt) => excerpt switch
    {
        WordExcerpt document => 64 + document.Paragraphs.Sum(p => 32L + (p.Text.Length * 2)),
        SheetExcerpt sheet => 64 + (sheet.Rows.Count * (long)sheet.ColumnCount * 24) + sheet.Rows.Sum(r => r.Sum(c => c.Text.Length * 2L)),
        TextExcerpt text => 64 + text.Lines.Sum(l => 32L + (l.Length * 2)),
        _ => 1024,
    };
}
