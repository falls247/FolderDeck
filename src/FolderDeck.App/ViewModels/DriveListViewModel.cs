using System.Collections.ObjectModel;
using FolderDeck.Core.Drives;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

/// <summary>
/// The drives of the sidebar's PC section, one row each, in the order of their letters.
/// <see cref="RefreshAsync"/> reads the list off the UI thread and then asks each drive whether it
/// is usable and what it is called. A drive that does not answer never holds up the others or the
/// UI: its row says so after 2 s and takes the answer whenever it arrives. Rows of drives that stay
/// are kept (an open cascade is anchored to one). Everything but the two shell calls runs on the UI
/// thread, which is where <see cref="RefreshAsync"/> must be called.
/// </summary>
public sealed class DriveListViewModel
{
    /// <summary>How long a drive may take to answer before its row reads 「応答がありません」.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IconCache _icons;

    /// <summary>Every drive of the last list, shown or not (an empty card reader slot is known but hidden).</summary>
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts the refreshes: a list that was read for an older one is dropped.</summary>
    private int _generation;

    /// <summary>A refresh that was overtaken asked for every drive to be probed again: the one that lands does it.</summary>
    private bool _recheckPending;

    public DriveListViewModel(IconCache icons)
    {
        _icons = icons;
    }

    /// <summary>The rows, by drive letter.</summary>
    public ObservableCollection<SidebarItemViewModel> Drives { get; } = [];

    /// <summary>
    /// A row left the list (the drive is gone, or its medium was taken out). Its element raises no
    /// MouseLeave, so whatever is anchored to the row has to be closed by whoever listens here.
    /// </summary>
    public event Action<SidebarItemViewModel>? RowRemoved;

    /// <summary>
    /// Brings the rows in line with the drives the system has now. New drives, and drives that were
    /// not usable the last time, are probed; <paramref name="recheck"/> probes every drive again
    /// (a label or a disc may have changed, a network drive may have gone away). Returns once the
    /// list is applied: the probes go on by themselves.
    /// </summary>
    public async Task RefreshAsync(bool recheck)
    {
        int generation = ++_generation;
        _recheckPending |= recheck;

        IReadOnlyList<DriveEntry> found;
        try
        {
            found = await Task.Run(DriveCatalog.Enumerate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The drives could not be listed.", ex);
            return;
        }

        if (generation != _generation)
        {
            return; // a later refresh is on its way with a newer list
        }

        recheck = _recheckPending;
        _recheckPending = false;

        var roots = new HashSet<string>(found.Select(d => d.Root), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _slots.Values.Where(s => !roots.Contains(s.Entry.Root)).ToList())
        {
            Drop(gone);
        }

        foreach (var entry in found)
        {
            if (_slots.TryGetValue(entry.Root, out var slot) && slot.Entry.Kind != entry.Kind)
            {
                // The letter now belongs to another kind of drive: nothing known about the old one holds.
                Drop(slot);
                slot = null;
            }

            bool probe = recheck;
            if (slot is null)
            {
                slot = new Slot(entry);
                _slots[entry.Root] = slot;
                probe = true;
            }
            else
            {
                // The list only knows letters, kinds and remembered mappings: what the last probe
                // found (label, readiness) stays, unless the list says the mapped drive is gone.
                bool lost = entry.IsReady == false && slot.Entry.IsReady != false;
                slot.Entry = slot.Entry with
                {
                    RemotePath = entry.RemotePath ?? slot.Entry.RemotePath,
                    IsReady = lost ? false : slot.Entry.IsReady,
                };
                probe |= slot.Entry.IsReady != true;
            }

            Sync(slot);
            if (probe)
            {
                StartProbe(slot);
            }
        }
    }

    /// <summary>One probe per drive at a time: a request that arrives meanwhile runs after it.</summary>
    private void StartProbe(Slot slot)
    {
        if (slot.Probing)
        {
            slot.ProbeAgain = true;
            return;
        }

        _ = ProbeAsync(slot);
    }

    private async Task ProbeAsync(Slot slot)
    {
        slot.Probing = true;
        try
        {
            do
            {
                slot.ProbeAgain = false;
                DriveEntry asked = slot.Entry;
                Task<DriveEntry> probe = Task.Run(() => DriveCatalog.Probe(asked));
                DriveEntry result;
                try
                {
                    result = await probe.WaitAsync(ProbeTimeout);
                }
                catch (TimeoutException)
                {
                    if (IsCurrent(slot))
                    {
                        slot.TimedOut = true;
                        Sync(slot);
                    }

                    // The call cannot be abandoned: it holds its pool thread until the system gives
                    // up. Waiting for it here keeps a second probe of this drive from piling up.
                    result = await probe;
                }

                if (!IsCurrent(slot))
                {
                    return; // the drive left the list meanwhile
                }

                slot.TimedOut = false;
                slot.Entry = result;
                Sync(slot);
            }
            while (slot.ProbeAgain);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Probe of drive {slot.Entry.Root} failed", ex);
        }
        finally
        {
            slot.Probing = false;
        }
    }

    /// <summary>The slot is still the one of its drive (not removed, not replaced by another kind of drive).</summary>
    private bool IsCurrent(Slot slot) =>
        _slots.TryGetValue(slot.Entry.Root, out var current) && ReferenceEquals(current, slot);

    /// <summary>Shows, updates or hides the row of a drive from what is known about it now.</summary>
    private void Sync(Slot slot)
    {
        DriveEntry entry = slot.Entry;
        if (!DriveRules.ShouldShow(entry.Kind, entry.IsReady))
        {
            RemoveRow(slot);
            return;
        }

        if (slot.Row is null)
        {
            slot.Row = SidebarItemViewModel.ForDrive(entry, _icons);
            Drives.Insert(IndexFor(entry.Root), slot.Row);
        }
        else
        {
            slot.Row.UpdateDrive(entry);
        }

        slot.Row.State = slot.TimedOut
            ? SidebarItemState.NoResponse
            : entry.IsReady switch
            {
                true => SidebarItemState.Ok,
                false => SidebarItemState.Missing,
                null => SidebarItemState.Checking,
            };
    }

    /// <summary>Where the row of <paramref name="root"/> goes: after the rows of the letters before it.</summary>
    private int IndexFor(string root)
    {
        int index = 0;
        while (index < Drives.Count && StringComparer.OrdinalIgnoreCase.Compare(Drives[index].Path, root) < 0)
        {
            index++;
        }

        return index;
    }

    private void Drop(Slot slot)
    {
        _slots.Remove(slot.Entry.Root);
        RemoveRow(slot);
    }

    private void RemoveRow(Slot slot)
    {
        if (slot.Row is not { } row)
        {
            return;
        }

        slot.Row = null;
        Drives.Remove(row);
        RowRemoved?.Invoke(row);
    }

    /// <summary>What is known about one drive letter.</summary>
    private sealed class Slot
    {
        public Slot(DriveEntry entry)
        {
            Entry = entry;
        }

        /// <summary>The drive as last listed and probed (its root and kind never change).</summary>
        public DriveEntry Entry { get; set; }

        /// <summary>The row, while the drive is shown.</summary>
        public SidebarItemViewModel? Row { get; set; }

        public bool Probing { get; set; }

        /// <summary>A probe was asked for while one was running.</summary>
        public bool ProbeAgain { get; set; }

        /// <summary>The running probe is past its timeout and has not answered yet.</summary>
        public bool TimedOut { get; set; }
    }
}
