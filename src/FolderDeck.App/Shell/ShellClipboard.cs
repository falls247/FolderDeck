using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Transfer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace FolderDeck.Shell;

/// <summary>
/// A file list read from the clipboard and the clipboard's sequence number right after the read
/// (zero when the number cannot be read). See <see cref="ShellClipboard.ClearIfUnchanged"/>.
/// </summary>
public sealed record ClipboardRead(ClipboardFiles Files, uint Sequence);

/// <summary>
/// The file list of the system clipboard (CF_HDROP plus "Preferred DropEffect"), as Explorer's
/// Ctrl+C / Ctrl+X write it and its Ctrl+V reads it. Raw Win32 rather than WPF's <c>Clipboard</c>:
/// the app needs the sequence number of its own write (to know later whether the clipboard is still
/// its own) and a read that can be given up on. The byte layouts are <see cref="FileClipboard"/>'s.
/// </summary>
public static class ShellClipboard
{
    /// <summary><c>CF_HDROP</c>.</summary>
    private const uint CfHDrop = 15;

    /// <summary>The whole wait for another program to close the clipboard before a write (UI thread).</summary>
    private const int WriteOpenBudgetMs = 100;

    /// <summary>The same wait before a read (worker thread; the caller's timeout still applies).</summary>
    private const int ReadOpenBudgetMs = 200;

    private const int RetryDelayMs = 10;

    /// <summary>A larger clipboard block is not a file list worth reading (a list of 10,000 long paths is about 5 MB).</summary>
    private const long MaxBlockBytes = 32L * 1024 * 1024;

    private static uint s_effectFormat;

    /// <summary>A read the caller gave up on; while it is still stuck in the clipboard owner, no new read starts.</summary>
    private static volatile Task? s_abandonedRead;

    /// <summary>
    /// The clipboard sequence number: it changes with every change of the clipboard's content, by any
    /// program. Zero means the number cannot be read (no access to the window station).
    /// </summary>
    public static uint Sequence => PInvoke.GetClipboardSequenceNumber();

    /// <summary>True when the clipboard offers a file list. Does not open the clipboard; fine on the UI thread.</summary>
    public static bool HasFiles() => PInvoke.IsClipboardFormatAvailable(CfHDrop);

    /// <summary>
    /// Replaces the clipboard's content with <paramref name="paths"/> as a copy or a cut (UI thread).
    /// <paramref name="owner"/> is the window that becomes the clipboard owner. The paths are shortened
    /// like a drag's (<see cref="FileDrag.DropList"/>). Waits at most 100 ms for another program to
    /// let go of the clipboard. On success <paramref name="sequence"/> is the clipboard's sequence
    /// number after the write (see <see cref="ClearIfOwned"/>); on failure the clipboard holds either
    /// its old content or nothing, never half of the new one.
    /// </summary>
    public static bool TryWrite(IReadOnlyList<string> paths, TransferKind kind, nint owner, out uint sequence)
    {
        sequence = 0;
        ArgumentNullException.ThrowIfNull(paths);

        // Without an owner window EmptyClipboard leaves the clipboard ownerless and SetClipboardData fails.
        if (owner == 0)
        {
            return false;
        }

        string[] drop = FileDrag.DropList(paths, ShellInterop.GetShortPath);
        if (drop.Length == 0)
        {
            return false;
        }

        uint effectFormat = EffectFormat();
        if (effectFormat == 0)
        {
            // A cut written without its effect would be pasted as a copy by every other program.
            AppLog.Warn("Clipboard write: the drop effect format could not be registered.");
            return false;
        }

        // Both blocks exist before the clipboard is touched, so running out of memory changes nothing.
        HGLOBAL files = Allocate(FileClipboard.EncodeHDrop(drop));
        HGLOBAL effect = Allocate(FileClipboard.EncodeDropEffect(kind));
        bool written = false;
        try
        {
            if (files.IsNull || effect.IsNull)
            {
                AppLog.Warn("Clipboard write: out of memory.");
                return false;
            }

            if (!Open(new HWND(owner), WriteOpenBudgetMs))
            {
                AppLog.Info("Clipboard write: another program kept the clipboard open.");
                return false;
            }

            try
            {
                if (!PInvoke.EmptyClipboard())
                {
                    AppLog.Warn("Clipboard write: the clipboard could not be emptied.");
                    return false;
                }

                written = Put(CfHDrop, ref files) && Put(effectFormat, ref effect);
                if (!written)
                {
                    // Never leave a file list without its effect behind (a cut would look like a copy).
                    AppLog.Warn("Clipboard write: the clipboard refused the data.");
                    PInvoke.EmptyClipboard();
                }
            }
            finally
            {
                PInvoke.CloseClipboard();
            }
        }
        finally
        {
            // Only the blocks the clipboard did not take are still ours (Put resets the others).
            Free(files);
            Free(effect);
        }

        if (!written)
        {
            return false;
        }

        // Read after CloseClipboard: this is the number the next reader sees for this content.
        sequence = PInvoke.GetClipboardSequenceNumber();
        return true;
    }

    /// <summary>
    /// The clipboard's file list and whether it is a cut, or null: no file list, an unreadable one,
    /// the clipboard stayed locked, or <paramref name="timeout"/> passed. The read runs on its own
    /// thread, because the program that owns the clipboard may render the list only when it is asked
    /// (Explorer does) and a hung owner blocks the reader for as long as it hangs. After a timeout
    /// that thread is left behind (it closes the clipboard once the owner answers), and further calls
    /// return null at once until it has ended. Never throws. To clear another program's cut after
    /// its items were moved, use <see cref="ReadWithSequenceAsync"/> instead.
    /// </summary>
    public static async Task<ClipboardFiles?> ReadAsync(TimeSpan timeout) =>
        (await ReadWithSequenceAsync(timeout).ConfigureAwait(false))?.Files;

    /// <summary>
    /// <see cref="ReadAsync"/> together with the clipboard's sequence number at the time of the read,
    /// for <see cref="ClearIfUnchanged"/>. The paths come back in their long spelling: a list this
    /// app wrote itself carries 8.3 short names for paths over MAX_PATH (see <see cref="TryWrite"/>),
    /// and <see cref="TransferRules"/> compares spellings only.
    /// </summary>
    public static async Task<ClipboardRead?> ReadWithSequenceAsync(TimeSpan timeout)
    {
        if (!HasFiles() || s_abandonedRead is { IsCompleted: false })
        {
            return null;
        }

        if (timeout < TimeSpan.Zero)
        {
            timeout = TimeSpan.Zero;
        }

        Task<ClipboardRead?> read = StaThread.Run<ClipboardRead?>(
            "FolderDeck clipboard read",
            Read,
            ex =>
            {
                AppLog.Warn("Clipboard read failed.", ex);
                return null;
            });
        try
        {
            return await read.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            s_abandonedRead = read;
            AppLog.Warn($"Clipboard read: the clipboard owner did not answer within {timeout.TotalMilliseconds:0} ms.");
            return null;
        }
    }

    /// <summary>
    /// Empties the clipboard if it still holds what <see cref="TryWrite"/> put there: the sequence
    /// number is still <paramref name="sequence"/> and <paramref name="owner"/> is still the clipboard
    /// owner. Used after the cut items were moved, so they cannot be pasted a second time. Does
    /// nothing when another program (or the user) changed the clipboard in the meantime (UI thread).
    /// </summary>
    public static void ClearIfOwned(uint sequence, nint owner)
    {
        if (sequence == 0 || owner == 0 || PInvoke.GetClipboardSequenceNumber() != sequence)
        {
            return;
        }

        if (!Open(new HWND(owner), WriteOpenBudgetMs))
        {
            return;
        }

        try
        {
            // Checked again now that nobody else can change it. The owner check covers a change
            // that slipped in between TryWrite's CloseClipboard and its reading of the number.
            if (PInvoke.GetClipboardSequenceNumber() == sequence && (nint)PInvoke.GetClipboardOwner() == owner)
            {
                PInvoke.EmptyClipboard();
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>
    /// Empties the clipboard if its content has not changed since a read: the sequence number is
    /// still <paramref name="sequence"/> (from <see cref="ReadWithSequenceAsync"/>). For a cut made
    /// by another program (Explorer) whose items this app has moved: the list would otherwise stay
    /// and a second paste, here or in Explorer, would try to move items that are gone. Unlike
    /// <see cref="ClearIfOwned"/> the clipboard owner is not checked, since it is the other program.
    /// <paramref name="owner"/> is the window that opens the clipboard (UI thread).
    /// </summary>
    public static void ClearIfUnchanged(uint sequence, nint owner)
    {
        if (sequence == 0 || owner == 0 || PInvoke.GetClipboardSequenceNumber() != sequence)
        {
            return;
        }

        if (!Open(new HWND(owner), WriteOpenBudgetMs))
        {
            return;
        }

        try
        {
            // Checked again now that nobody else can change it.
            if (PInvoke.GetClipboardSequenceNumber() == sequence)
            {
                PInvoke.EmptyClipboard();
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>
    /// The long spelling of a path that may contain 8.3 short names (<c>E:\LONGFO~1\x</c>), or the
    /// path itself: when it has no component that looks like a short name, when the item is gone, or
    /// when the system cannot expand it. Asks the file system (and the server, for a network path),
    /// so not for the UI thread.
    /// </summary>
    internal static string ToLongPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !MayHaveShortName(path))
        {
            return path;
        }

        try
        {
            // The prefix lifts the MAX_PATH limit of the result, which is the reason the short form
            // was used in the first place. It is taken off again below.
            bool prefixed = path.StartsWith(@"\\?\", StringComparison.Ordinal);
            string input = prefixed ? path : (path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path);
            char[] buffer = new char[2048];
            uint length = PInvoke.GetLongPathName(input, buffer);
            if (length > buffer.Length)
            {
                // The size that is needed, terminator included.
                buffer = new char[length];
                length = PInvoke.GetLongPathName(input, buffer);
            }

            if (length == 0 || length > buffer.Length)
            {
                return path;
            }

            string result = new(buffer, 0, (int)length);
            if (prefixed)
            {
                return result;
            }

            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + result[8..];
            }

            return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return path;
        }
    }

    /// <summary>A generated 8.3 name always has a tilde followed by a digit (<c>LONGFO~1</c>).</summary>
    private static bool MayHaveShortName(string path)
    {
        for (int i = path.IndexOf('~'); i >= 0 && i + 1 < path.Length; i = path.IndexOf('~', i + 1))
        {
            if (char.IsAsciiDigit(path[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    // ---- read (worker thread) ------------------------------------------------------------------

    private static ClipboardRead? Read()
    {
        // No owner window: this thread has none, and a reader does not need one.
        if (!Open(HWND.Null, ReadOpenBudgetMs))
        {
            AppLog.Info("Clipboard read: another program kept the clipboard open.");
            return null;
        }

        IReadOnlyList<string> paths;
        TransferKind kind = TransferKind.Copy;
        uint sequence;
        try
        {
            // May block: the owner renders the list now.
            byte[]? files = ReadBlock(CfHDrop);
            if (files is null)
            {
                return null;
            }

            paths = FileClipboard.DecodeHDrop(files);
            if (paths.Count == 0)
            {
                return null;
            }

            // No effect on the clipboard means a copy (a file list written by a program other than Explorer).
            uint effectFormat = EffectFormat();
            if (effectFormat != 0 && PInvoke.IsClipboardFormatAvailable(effectFormat) && ReadBlock(effectFormat) is { } effect)
            {
                kind = FileClipboard.DecodeDropEffect(effect);
            }

            // After both blocks, still inside the open clipboard: an owner that renders its data
            // only when asked advances the number while doing so.
            sequence = PInvoke.GetClipboardSequenceNumber();
        }
        finally
        {
            PInvoke.CloseClipboard();
        }

        // With the clipboard closed again: this asks the file system.
        var expanded = new string[paths.Count];
        for (int i = 0; i < expanded.Length; i++)
        {
            expanded[i] = ToLongPath(paths[i]);
        }

        return new ClipboardRead(new ClipboardFiles(expanded, kind), sequence);
    }

    /// <summary>
    /// A copy of one clipboard format's bytes, or null. The clipboard must be open. The handle stays
    /// the clipboard's: it is never freed here.
    /// </summary>
    private static unsafe byte[]? ReadBlock(uint format)
    {
        HANDLE data = PInvoke.GetClipboardData(format);
        if (data.IsNull)
        {
            return null;
        }

        var memory = new HGLOBAL((nint)data);

        // The size may be rounded up past what the owner wrote; the decoders stop at the terminators.
        ulong size = PInvoke.GlobalSize(memory);
        if (size == 0 || size > (ulong)MaxBlockBytes)
        {
            return null;
        }

        void* bytes = PInvoke.GlobalLock(memory);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return new ReadOnlySpan<byte>(bytes, (int)size).ToArray();
        }
        finally
        {
            PInvoke.GlobalUnlock(memory);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The id of "Preferred DropEffect" (the same in every process of the session), or 0.</summary>
    private static uint EffectFormat()
    {
        uint format = s_effectFormat;
        if (format == 0)
        {
            format = PInvoke.RegisterClipboardFormat(FileClipboard.PreferredDropEffect);
            s_effectFormat = format;
        }

        return format;
    }

    /// <summary>
    /// <c>OpenClipboard</c>, retried while another program has the clipboard open, for at most
    /// <paramref name="budgetMs"/> in total.
    /// </summary>
    private static bool Open(HWND owner, int budgetMs)
    {
        long start = Environment.TickCount64;
        while (true)
        {
            if (PInvoke.OpenClipboard(owner))
            {
                return true;
            }

            if (Environment.TickCount64 - start + RetryDelayMs > budgetMs)
            {
                return false;
            }

            Thread.Sleep(RetryDelayMs);
        }
    }

    /// <summary>A movable global block holding <paramref name="bytes"/> (what the clipboard takes), or a null handle.</summary>
    private static unsafe HGLOBAL Allocate(byte[] bytes)
    {
        HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (memory.IsNull)
        {
            return default;
        }

        void* target = PInvoke.GlobalLock(memory);
        if (target is null)
        {
            PInvoke.GlobalFree(memory);
            return default;
        }

        bytes.CopyTo(new Span<byte>(target, bytes.Length));
        PInvoke.GlobalUnlock(memory);
        return memory;
    }

    /// <summary>
    /// <c>SetClipboardData</c>. On success the block belongs to the system and
    /// <paramref name="memory"/> is reset, so the caller cannot free it; on failure it stays the
    /// caller's to free.
    /// </summary>
    private static bool Put(uint format, ref HGLOBAL memory)
    {
        HANDLE taken = PInvoke.SetClipboardData(format, new HANDLE((nint)memory));
        if (taken.IsNull)
        {
            return false;
        }

        memory = default;
        return true;
    }

    private static void Free(HGLOBAL memory)
    {
        if (!memory.IsNull)
        {
            PInvoke.GlobalFree(memory);
        }
    }
}
