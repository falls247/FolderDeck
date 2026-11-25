using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace FolderDeck.Core.Paths;

/// <summary>
/// Who a folder is, apart from where it is: the serial number of its volume, its file id and its
/// creation time, kept as a string. The file id is the 128-bit one (<c>v1:…</c>, NTFS and ReFS) or,
/// where the file system does not give that one, the 64-bit file index (<c>i1:…</c>: the shares of
/// WSL refuse the 128-bit id to .NET processes); the two kinds are never compared with each other. A
/// rename or a move on the same volume keeps all three; a folder deleted and made again under the
/// same name has another id, and the creation time stops an id that the file system gave again (WSL
/// reuses inode numbers) from passing for the old folder. Lets 登録フォルダ and open tabs follow a
/// folder renamed or moved outside the app. Blocking I/O: call off the UI thread.
/// </summary>
public static class FolderIdentity
{
    /// <summary>At most this many subfolders of the old parent are looked at (a parent on a slow share must not keep the search going).</summary>
    public const int MaxScan = 2000;

    // Asks for nothing but the attributes: no read access, no lock, no hydration of a cloud folder.
    private const uint ReadAttributes = (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES;
    private const FILE_SHARE_MODE ShareAll = FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    // A folder needs backup semantics to be opened at all; a junction or link is taken as itself.
    private const FILE_FLAGS_AND_ATTRIBUTES OpenFlags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;

    private const uint AttributeDirectory = 0x10;

    /// <summary>The identity of the folder at <paramref name="path"/>, or null (not a folder, gone, or the file system has no ids).</summary>
    public static string? Capture(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using var handle = Open(path);
            return !handle.IsInvalid && TryRead(handle, out var key) ? key.ToString() : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the folder of <paramref name="identity"/> is now, given where it was
    /// (<paramref name="lastKnownPath"/>, which no longer leads to it). First among the folders of
    /// the old parent (a rename in place; the path keeps its form, e.g. a mapped drive letter), then
    /// by its id on the old volume (a renamed ancestor, a move). Null when it is not found, is the
    /// old path after all, or is in the Recycle Bin (deleted is not moved).
    /// </summary>
    public static string? Locate(string identity, string lastKnownPath)
    {
        if (!FolderKey.TryParse(identity, out var key) || string.IsNullOrEmpty(lastKnownPath))
        {
            return null;
        }

        string? found;
        try
        {
            found = LocateInParent(key, lastKnownPath) ?? LocateById(key, lastKnownPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }

        return found is not null && IsUsable(found) && !PathTools.PathEquals(found, lastKnownPath) ? found : null;
    }

    /// <summary>A place a folder may be followed to: not the Recycle Bin, not the volume's system folder.</summary>
    internal static bool IsUsable(string path)
    {
        foreach (string part in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static string? LocateInParent(FolderKey key, string lastKnownPath)
    {
        string parent = PathTools.ParentOf(lastKnownPath);
        if (parent.Length == 0 || !Directory.Exists(parent))
        {
            return null;
        }

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0, // hidden and system folders too: a folder may have been hidden as well as renamed
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        int seen = 0;
        foreach (string folder in Directory.EnumerateDirectories(parent, "*", options))
        {
            if (++seen > MaxScan)
            {
                break;
            }

            using var handle = Open(folder);
            if (!handle.IsInvalid && TryRead(handle, out var other) && other == key)
            {
                return PathTools.NormalizeOrSelf(folder);
            }
        }

        return null;
    }

    internal static unsafe string? LocateById(FolderKey key, string lastKnownPath)
    {
        string root = Path.GetPathRoot(lastKnownPath) ?? string.Empty;
        if (root.Length == 0)
        {
            return null;
        }

        // Any handle on the volume will do as the hint; the volume must still be the one the id is from
        // (another disk may have the drive letter now).
        using var hint = Open(root);
        if (hint.IsInvalid || !TryRead(hint, out var rootKey) || rootKey.Extended != key.Extended || rootKey.Volume != key.Volume)
        {
            return null;
        }

        var descriptor = new FILE_ID_DESCRIPTOR { dwSize = (uint)sizeof(FILE_ID_DESCRIPTOR) };
        if (key.Extended)
        {
            descriptor.Type = FILE_ID_TYPE.ExtendedFileIdType;
            Unsafe.As<FILE_ID_128, UInt128>(ref descriptor.Anonymous.ExtendedFileId) = key.FileId;
        }
        else
        {
            descriptor.Type = FILE_ID_TYPE.FileIdType;
            descriptor.Anonymous.FileId = (long)(ulong)key.FileId;
        }

        using var handle = PInvoke.OpenFileById(hint, in descriptor, ReadAttributes, ShareAll, null, OpenFlags);
        if (handle.IsInvalid || !TryRead(handle, out var found) || found != key)
        {
            return null; // gone, or a file system that cannot open by id (FAT, many shares)
        }

        string? path = FinalPath(handle);

        // The same drive as before: a mapped drive is reported in its UNC form, which is not followed.
        return path is not null && PathTools.PathEquals(Path.GetPathRoot(path), root) ? path : null;
    }

    private static SafeFileHandle Open(string path) =>
        PInvoke.CreateFile(Extended(path), ReadAttributes, ShareAll, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, OpenFlags, null);

    /// <summary>
    /// The key of the folder behind <paramref name="handle"/>: the 128-bit id when the file system
    /// gives it, else the 64-bit file index. Always the same kind for the same file system, so a key
    /// read now compares with one captured before.
    /// </summary>
    private static unsafe bool TryRead(SafeFileHandle handle, out FolderKey key)
    {
        key = default;
        FILE_BASIC_INFO basic;
        if (!PInvoke.GetFileInformationByHandleEx(Raw(handle), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO))
            || (basic.FileAttributes & AttributeDirectory) == 0)
        {
            return false;
        }

        FILE_ID_INFO id;
        if (PInvoke.GetFileInformationByHandleEx(Raw(handle), FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)))
        {
            UInt128 fileId = Unsafe.As<FILE_ID_128, UInt128>(ref id.FileId);
            if (fileId != UInt128.Zero)
            {
                key = new FolderKey(true, id.VolumeSerialNumber, fileId, basic.CreationTime);
                return true;
            }
        }

        if (!PInvoke.GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
        {
            return false;
        }

        ulong index = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
        if (index == 0)
        {
            return false; // a file system without ids
        }

        key = new FolderKey(false, info.dwVolumeSerialNumber, index, basic.CreationTime);
        return true;
    }

    /// <summary>The handle's path as the system names it, without the <c>\\?\</c> prefix (<c>\\?\UNC\server\share</c> → <c>\\server\share</c>).</summary>
    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* text = buffer)
            {
                length = PInvoke.GetFinalPathNameByHandle(Raw(handle), text, (uint)buffer.Length, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                string path = new(buffer, 0, (int)length);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    path = @"\\" + path[8..];
                }
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    path = path[4..];
                }

                return PathTools.NormalizeOrSelf(path);
            }

            if (length > 32768)
            {
                return null;
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>The raw handle for a call that takes one; <paramref name="handle"/> stays open around the call (its <c>using</c>).</summary>
    private static HANDLE Raw(SafeFileHandle handle) => (HANDLE)handle.DangerousGetHandle();

    /// <summary>The path for CreateFile: with the <c>\\?\</c> prefix once it is too long for the plain form.</summary>
    private static string Extended(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}

/// <summary>
/// The parts of a <see cref="FolderIdentity"/>: which kind of id (<paramref name="Extended"/>: the
/// 128-bit file id, else the 64-bit file index), the volume serial number, the id, the creation time
/// (FILETIME ticks). Written <c>v1:</c> / <c>i1:</c> + volume + id + time in hex.
/// </summary>
internal readonly record struct FolderKey(bool Extended, ulong Volume, UInt128 FileId, long Created)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(Extended ? "v1" : "i1")}:{Volume:x16}:{FileId:x32}:{Created:x16}");

    public static bool TryParse(string? text, out FolderKey key)
    {
        key = default;
        bool extended = text is not null && text.StartsWith("v1:", StringComparison.Ordinal);
        if (text is null || !(extended || text.StartsWith("i1:", StringComparison.Ordinal)))
        {
            return false;
        }

        string[] parts = text[3..].Split(':');
        if (parts.Length != 3
            || !ulong.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong volume)
            || !UInt128.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out UInt128 fileId)
            || !long.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long created)
            || fileId == UInt128.Zero)
        {
            return false;
        }

        key = new FolderKey(extended, volume, fileId, created);
        return true;
    }
}
