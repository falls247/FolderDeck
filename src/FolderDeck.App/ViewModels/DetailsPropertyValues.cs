using System.ComponentModel;
using System.Windows.Threading;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

/// <summary>
/// The values of one item's extra details columns, bound by the row as <c>Props[id]</c>. The getter
/// only looks in the cache (UI thread); what is missing is gathered for the rest of the dispatcher
/// pass and read in one go (one opening of the item's property store for all the row's shown
/// columns), then "Item[]" changes. Only the shown columns are bound (DetailsRowView's triggers),
/// so nothing is read while every extra column is hidden. A row whose container is reused for
/// another item cancels what it had not started yet (<see cref="CancelPending"/>).
/// </summary>
public sealed class DetailsPropertyValues(CardViewModel card, ShellPropertyCache cache) : INotifyPropertyChanged
{
    private readonly string _key = ShellPropertyCache.KeyOf(card.Path, card.ModifiedUtc);
    private HashSet<string>? _pending;
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private bool _flushQueued;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string id]
    {
        get
        {
            if (card.IsDisabled || !(card.IsFile || card.IsFolder))
            {
                return string.Empty;
            }

            if (cache.TryGet(_key, id, out string text))
            {
                return text;
            }

            if (!_inFlight.Contains(id) && (_pending ??= new(StringComparer.Ordinal)).Add(id) && !_flushQueued)
            {
                _flushQueued = true;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
            }

            return string.Empty;
        }
    }

    /// <summary>The row went to another item (or away): what has not started is not read for it.</summary>
    public void CancelPending()
    {
        _cts?.Cancel();
        _cts = null;
        _pending = null;
        _inFlight.Clear();
    }

    private async void Flush()
    {
        _flushQueued = false;
        if (_pending is not { Count: > 0 } pending)
        {
            return;
        }

        _pending = null;
        var ids = pending.ToList();
        _inFlight.UnionWith(ids);
        var cts = _cts ??= new CancellationTokenSource();
        try
        {
            await cache.GetAsync(card.Path, _key, ids, cts.Token);
            if (!ReferenceEquals(cts, _cts))
            {
                return; // cancelled meanwhile
            }

            _inFlight.ExceptWith(ids);
            if (cache.TryGet(_key, ids[0], out _))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); // not after a full queue: no retry loop
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Properties of {card.Path} could not be read", ex);
        }
    }
}
