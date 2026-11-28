using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Listing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace FolderDeck.Shell;

/// <summary>
/// The values of the details view's extra columns (<see cref="DetailsPropertyColumns"/>): read from
/// the item's property store (<c>SHGetPropertyStoreFromParsingName</c>) and formatted by Windows as
/// Explorer shows them (<c>PSFormatForDisplay</c>), on workers of their own (property handlers open
/// the file and may hang: they must not hold up the type names, thumbnails or other shell calls).
/// Only for rows that are realized and only for the shown columns (see <c>CardViewModel.Props</c>).
/// A file that is only in the cloud is read with the fast properties only (no download). The store
/// is released at once: a handler keeps the file open, and a rename or a delete would find it in use.
/// Cached by path and write time; failures are cached as empty (a queue timeout is not).
/// </summary>
public sealed class ShellPropertyCache
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly StaWorkerPool _worker;
    private readonly TtlCache<string, Dictionary<string, string>> _cache = new(TimeSpan.FromMinutes(30), 8000, StringComparer.OrdinalIgnoreCase);
    private readonly object _keysGate = new();
    private readonly Dictionary<string, PROPERTYKEY?> _keys = new(StringComparer.Ordinal);
    private Task<IReadOnlyDictionary<string, string>>? _titles;

    public ShellPropertyCache(StaWorkerPool worker)
    {
        _worker = worker;
    }

    /// <summary>The cache key of an item as listed: a write changes it (the listing makes a new card then too).</summary>
    public static string KeyOf(string path, DateTime writeUtc) => $"{path}|{writeUtc.Ticks}";

    /// <summary>The value of column <paramref name="id"/> when it was read already (UI thread).</summary>
    public bool TryGet(string key, string id, out string text)
    {
        if (_cache.TryGet(key, out var values) && values.TryGetValue(id, out string? found))
        {
            text = found;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads <paramref name="ids"/> of <paramref name="path"/> (one opening of its store for all of
    /// them) and keeps them with what was read before. A call not started when
    /// <paramref name="cancellationToken"/> fires is dropped (a row scrolled away).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(string path, string key, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        Dictionary<string, string> read;
        try
        {
            read = await _worker.RunAsync(() => Read(path, ids), cancellationToken, CallTimeout).ConfigureAwait(true);
        }
        catch (QueueTimeoutException)
        {
            return new Dictionary<string, string>(); // not cached: the queue was full, the item is fine
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure of the store or a handler (an HRESULT maps to ArgumentException, NotImplementedException… as
            // well as COMException): cached as empty, so a row that comes back does not open the file again.
            read = ids.ToDictionary(id => id, _ => string.Empty, StringComparer.Ordinal);
        }

        var merged = _cache.TryGet(key, out var before) ? new Dictionary<string, string>(before, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in read)
        {
            merged[pair.Key] = pair.Value;
        }

        _cache.Set(key, merged);
        return merged;
    }

    /// <summary>Windows' own names of the extra columns (as Explorer's headers), read once; the built-in titles where none comes.</summary>
    public Task<IReadOnlyDictionary<string, string>> TitlesAsync() =>
        _titles ??= _worker.RunAsync(ReadTitles, CancellationToken.None, CallTimeout);

    private unsafe IReadOnlyDictionary<string, string> ReadTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in DetailsPropertyColumns.All)
        {
            titles[column.Id] = column.Title;
            if (KeyFor(column) is not { } key)
            {
                continue;
            }

            try
            {
                PROPERTYKEY k = key;
                Guid iid = typeof(IPropertyDescription).GUID;
                PInvoke.PSGetPropertyDescription(&k, &iid, out object raw).ThrowOnFailure();
                var description = (IPropertyDescription)raw;
                try
                {
                    description.GetDisplayName(out PWSTR name);
                    if (name.Value != null)
                    {
                        string text = name.ToString();
                        PInvoke.CoTaskMemFree(name.Value);
                        if (text.Length > 0)
                        {
                            titles[column.Id] = text;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(description);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                AppLog.Info($"Property title of {column.CanonicalName} not read: {ex.Message}");
            }
        }

        return titles;
    }

    private PROPERTYKEY? KeyFor(DetailsPropertyColumn column)
    {
        lock (_keysGate)
        {
            if (!_keys.TryGetValue(column.Id, out var key))
            {
                key = PInvoke.PSGetPropertyKeyFromName(column.CanonicalName, out PROPERTYKEY found).Succeeded ? found : null;
                _keys[column.Id] = key;
            }

            return key;
        }
    }

    private unsafe Dictionary<string, string> Read(string path, IReadOnlyList<string> ids)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            values[id] = string.Empty;
        }

        var flags = FileContent.IsCloudOnly(path)
            ? GETPROPERTYSTOREFLAGS.GPS_FASTPROPERTIESONLY
            : GETPROPERTYSTOREFLAGS.GPS_BESTEFFORT;
        Guid storeIid = typeof(IPropertyStore).GUID;
        object raw;
        fixed (char* shellPath = ShellActions.ShellPath(path))
        {
            PInvoke.SHGetPropertyStoreFromParsingName(new PCWSTR(shellPath), null, flags, &storeIid, out raw).ThrowOnFailure();
        }

        var store = (IPropertyStore)raw;
        try
        {
            foreach (string id in ids)
            {
                if (DetailsPropertyColumns.ById(id) is not { } column || KeyFor(column) is not { } key)
                {
                    continue;
                }

                PROPVARIANT value = default;
                try
                {
                    store.GetValue(in key, out value);
                    if (value.Anonymous.Anonymous.vt == Windows.Win32.System.Variant.VARENUM.VT_EMPTY)
                    {
                        continue;
                    }

                    if (PInvoke.PSFormatForDisplayAlloc(in key, in value, PROPDESC_FORMAT_FLAGS.PDFF_DEFAULT, out PWSTR text).Succeeded && text.Value != null)
                    {
                        values[id] = text.ToString();
                        PInvoke.CoTaskMemFree(text.Value);
                    }
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or NotImplementedException or InvalidCastException)
                {
                    // this property cannot be read for this item (a handler's HRESULT): left empty, the others are still read
                }
                finally
                {
                    PInvoke.PropVariantClear(ref value);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store); // at once: the handler may keep the file open
        }

        return values;
    }
}
