using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Diagnostics.Debug;

namespace FolderDeck.Shell;

/// <summary>
/// What <see cref="TransferRules.Decide"/> needs to know about the sources of a paste or a drop:
/// the kind of media each lies on, and whether it could be removed there (a move deletes its
/// source). The only caller of <c>GetDriveType</c> / <c>GetVolumeInformation</c> for sources.
/// <para>
/// Read-only by construction: the delete check opens the item asking for the right to delete and
/// closes it again. Nothing is deleted, renamed, written or marked for deletion
/// (<c>FILE_FLAG_DELETE_ON_CLOSE</c> is never passed).
/// </para>
/// <para>
/// The calls can block on a dead server or a sleeping disk, so they run on a thread of their own
/// and the caller gets an answer after <c>timeout</c> at the latest; the thread is then left to
/// finish (or hang) by itself.
/// </para>
/// </summary>
public static class SourceProbe
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorWriteProtect = 19;

    /// <summary>
    /// From this length on, a path gets the <c>\\?\</c> prefix so that <c>CreateFile</c> accepts it
    /// whatever the long-path setting of the system is.
    /// </summary>
    private const int LongPathThreshold = 248;

    /// <summary>
    /// One <see cref="TransferSource"/> per path, in the same order and with the path as it was given.
    /// <list type="bullet">
    /// <item>Media: a UNC path or a mapped network drive is <see cref="SourceMedia.Network"/>; an
    /// optical drive or a read-only volume is <see cref="SourceMedia.ReadOnly"/>.</item>
    /// <item><c>CanDelete</c> is false when opening the item with delete access is refused
    /// (<c>ERROR_ACCESS_DENIED</c>, <c>ERROR_WRITE_PROTECT</c>), on read-only media, and for every
    /// source not reached before <paramref name="timeout"/>. An item that is merely in use
    /// (<c>ERROR_SHARING_VIOLATION</c>) counts as deletable: the shell asks about it during the move.</item>
    /// <item>With <paramref name="probeNetwork"/> false, network sources are not opened at all
    /// (no round trip to a server during a drag); their <c>CanDelete</c> is true, meaning "not known
    /// to be refused".</item>
    /// </list>
    /// Never faults.
    /// </summary>
    public static async Task<IReadOnlyList<TransferSource>> DescribeAsync(IReadOnlyList<string> paths, bool probeNetwork, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return Array.Empty<TransferSource>();
        }

        var run = new Run(paths, probeNetwork);
        Task work = Task.Factory.StartNew(run.Execute, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await work.WaitAsync(Clamp(timeout)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            int left = run.Abandon();
            if (left > 0)
            {
                AppLog.Warn($"SourceProbe: no answer within {timeout}; {left} of {paths.Count} sources not checked.");
            }
        }

        return run.Snapshot();
    }

    /// <summary>
    /// <c>Directory.Exists</c> that gives up: false when the folder is missing, is not a folder, or
    /// did not answer within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<bool> FolderExistsAsync(string path, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        Task<bool> work = Task.Factory.StartNew(() => Directory.Exists(path), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            return await work.WaitAsync(Clamp(timeout)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"SourceProbe: no answer within {timeout}: {path}");
            return false;
        }
    }

    /// <summary><c>WaitAsync</c> rejects negative values other than the infinite one.</summary>
    private static TimeSpan Clamp(TimeSpan timeout) =>
        timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan ? TimeSpan.Zero : timeout;

    private static TransferSource Describe(string path, bool probeNetwork, Dictionary<string, SourceMedia> mediaByRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || !PathTools.IsFullyQualified(path))
        {
            // Not something to open; TransferRules refuses it by its text.
            return new TransferSource(path, SourceMedia.Local, false);
        }

        SourceMedia media = MediaOf(path, mediaByRoot);
        return media switch
        {
            // Nothing can be removed there, and opening an item could spin up a disc for no gain.
            SourceMedia.ReadOnly => new TransferSource(path, media, false),
            SourceMedia.Network when !probeNetwork => new TransferSource(path, media, true),
            _ => new TransferSource(path, media, CanDelete(path)),
        };
    }

    private static SourceMedia MediaOf(string path, Dictionary<string, SourceMedia> mediaByRoot)
    {
        if (TransferRules.IsNetworkPath(path))
        {
            return SourceMedia.Network;
        }

        string root = (Path.GetPathRoot(path) ?? string.Empty).Replace('/', '\\');
        if (root.Length == 0)
        {
            return SourceMedia.Local;
        }

        if (!root.EndsWith('\\'))
        {
            root += '\\'; // GetDriveType wants the trailing separator
        }

        if (!mediaByRoot.TryGetValue(root, out SourceMedia media))
        {
            media = VolumeMedia(root);
            mediaByRoot[root] = media;
        }

        return media;
    }

    /// <summary>Kind of the volume behind a drive root (<c>E:\</c>).</summary>
    private static unsafe SourceMedia VolumeMedia(string root)
    {
        fixed (char* rootName = root)
        {
            var name = new PCWSTR(rootName);

            // By the drive letter alone: no medium and no server is contacted.
            uint type = PInvoke.GetDriveType(name);
            if (type == PInvoke.DRIVE_REMOTE)
            {
                return SourceMedia.Network;
            }

            if (type == PInvoke.DRIVE_CDROM)
            {
                return SourceMedia.ReadOnly;
            }

            // Only the flags are asked for (a write-protected card or stick, a read-only mount).
            uint flags = 0;
            bool ok = PInvoke.GetVolumeInformation(name, default(PWSTR), 0, null, null, &flags, default(PWSTR), 0);
            return ok && (flags & PInvoke.FILE_READ_ONLY_VOLUME) != 0 ? SourceMedia.ReadOnly : SourceMedia.Local;
        }
    }

    /// <summary>
    /// Opens the item asking for delete access, sharing everything with other handles, and closes it
    /// at once. A link or junction is opened itself, not its target; a folder needs
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c> to be opened at all. The handle is used for nothing else:
    /// the item is not modified.
    /// </summary>
    private static unsafe bool CanDelete(string path)
    {
        string target = Extended(path);
        HANDLE handle;
        int error;
        fixed (char* fileName = target)
        {
            handle = PInvoke.CreateFile(
                new PCWSTR(fileName),
                (uint)FILE_ACCESS_RIGHTS.DELETE,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
                default(HANDLE));
            error = Marshal.GetLastPInvokeError();
        }

        nint raw = (nint)handle.Value;
        if (raw != 0 && raw != -1)
        {
            PInvoke.CloseHandle(handle);
            return true;
        }

        // Refused outright: the move could not remove the source. Every other failure (in use by
        // another program, already gone, a path the system rejects) is left to the shell, which
        // reports it when the transfer runs.
        return error is not (ErrorAccessDenied or ErrorWriteProtect);
    }

    private static string Extended(string path)
    {
        if (path.Length < LongPathThreshold
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        // The prefix turns normalization off, so it is done here first.
        string full = PathTools.NormalizeOrSelf(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>One <see cref="DescribeAsync"/> call: the worker fills the results, the caller may stop waiting.</summary>
    private sealed class Run
    {
        private readonly object _gate = new();
        private readonly string[] _paths;
        private readonly bool _probeNetwork;
        private readonly TransferSource[] _results;
        private int _done;
        private volatile bool _abandoned;

        public Run(IReadOnlyList<string> paths, bool probeNetwork)
        {
            _paths = paths.Select(p => p ?? string.Empty).ToArray();
            _probeNetwork = probeNetwork;
            _results = Unknown(_paths);
        }

        /// <summary>Runs on the probe thread. Never throws.</summary>
        public unsafe void Execute()
        {
            // GetVolumeInformation and CreateFile are raw calls: without this, a drive without a
            // medium (an empty card reader) can raise the system's "no disk in the drive" box when
            // the process was started with an error mode of 0. The .NET file APIs set the same mode
            // around their own calls. Not restored: the thread serves this one run only.
            THREAD_ERROR_MODE previous;
            PInvoke.SetThreadErrorMode(THREAD_ERROR_MODE.SEM_FAILCRITICALERRORS, &previous);

            var mediaByRoot = new Dictionary<string, SourceMedia>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _paths.Length && !_abandoned; i++)
            {
                TransferSource source;
                try
                {
                    source = Describe(_paths[i], _probeNetwork, mediaByRoot);
                }
                catch (Exception ex)
                {
                    // Keeps the cautious answer for this source and goes on with the rest.
                    AppLog.Warn($"SourceProbe failed: {_paths[i]}", ex);
                    continue;
                }

                lock (_gate)
                {
                    _results[i] = source;
                    _done++;
                }
            }
        }

        /// <summary>Stops the worker after the call it is in. Returns how many sources it did not reach.</summary>
        public int Abandon()
        {
            _abandoned = true;
            lock (_gate)
            {
                return _paths.Length - _done;
            }
        }

        public IReadOnlyList<TransferSource> Snapshot()
        {
            lock (_gate)
            {
                return (TransferSource[])_results.Clone();
            }
        }

        /// <summary>
        /// The answer for a source that was never reached: media by the text of the path alone, and
        /// not deletable, so a move it cannot vouch for becomes a copy.
        /// </summary>
        private static TransferSource[] Unknown(string[] paths)
        {
            var results = new TransferSource[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                results[i] = new TransferSource(paths[i], TransferRules.IsNetworkPath(paths[i]) ? SourceMedia.Network : SourceMedia.Local, false);
            }

            return results;
        }
    }
}
