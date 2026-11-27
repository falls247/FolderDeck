using System.Runtime.InteropServices;
using FolderDeck.Core.Drives;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WNet;

namespace FolderDeck.Shell;

/// <summary>
/// The drives of the PC section. <see cref="Enumerate"/> only lists them (letters, kinds and the
/// remembered network mappings): it never touches a medium, so an empty card reader, a sleeping disk
/// or an unreachable server cannot hold it up. <see cref="Probe"/> asks one drive whether it is
/// usable and what it is called; that may block (a network drive whose server is gone), so the
/// caller runs it off the UI thread and gives up after its own timeout.
/// </summary>
public static class DriveCatalog
{
    private const uint NoError = 0;

    /// <summary><c>ERROR_MORE_DATA</c>: the buffer is too small; the required size is returned.</summary>
    private const uint ErrorMoreData = 234;

    /// <summary>Bytes handed to <c>WNetEnumResource</c> per call (the size its documentation recommends).</summary>
    private const int EnumBufferBytes = 16 * 1024;

    /// <summary>Guard against an enumeration that never reports its end.</summary>
    private const int MaxEnumCalls = 64;

    /// <summary>
    /// Every drive letter in use, plus the remembered network connections that have a drive letter
    /// (a mapped drive that was not reconnected at sign-in may be missing from the first list).
    /// Call it off the UI thread. No media access: <c>IsReady</c> is null (not probed yet), except
    /// for a remembered connection that is not among the drive letters, which starts as false.
    /// Labels are always null here; <see cref="Probe"/> fills them in.
    /// </summary>
    public static IReadOnlyList<DriveEntry> Enumerate()
    {
        var byLetter = new Dictionary<char, DriveEntry>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                // Name and DriveType only (GetLogicalDrives / GetDriveType): neither reads the medium.
                // IsReady, VolumeLabel, sizes and the format would, so they are left to Probe.
                if (LetterOf(drive.Name) is not { } letter || KindOf(drive.DriveType) is not { } kind)
                {
                    continue;
                }

                byLetter[letter] = new DriveEntry(RootOf(letter), kind, null, null, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("DriveCatalog: the drives could not be listed.", ex);
        }

        foreach (var (letter, remote) in RememberedConnections())
        {
            if (!byLetter.TryGetValue(letter, out DriveEntry? known))
            {
                // Remembered but not connected in this session: listed as unavailable until probed.
                byLetter[letter] = new DriveEntry(RootOf(letter), DriveKind.Network, null, remote, false);
            }
            else if (known.Kind == DriveKind.Network)
            {
                byLetter[letter] = known with { RemotePath = remote };
            }

            // Otherwise the letter now belongs to a local drive: the drive that is there wins.
        }

        return DriveRules.Order(byLetter.Values);
    }

    /// <summary>
    /// Asks the drive itself: <c>IsReady</c>, the volume label and, for a network drive, the path it
    /// is mapped to. May block for as long as the system waits for the device or the server; the
    /// caller applies the timeout. Never throws for an unusable drive: it comes back with
    /// <c>IsReady = false</c>.
    /// </summary>
    public static DriveEntry Probe(DriveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        bool ready = false;
        string? label = null;
        try
        {
            var info = new DriveInfo(entry.Root);
            ready = info.IsReady;
            if (ready)
            {
                label = info.VolumeLabel;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not a drive root, or the medium went away between the two calls. A ready drive whose
            // label cannot be read stays ready, without a label.
        }

        string? remote = entry.RemotePath;
        if (entry.Kind == DriveKind.Network && LetterOf(entry.Root) is { } letter)
        {
            // A disconnected drive has no current connection: keep the remembered path.
            remote = ConnectionOf(letter) ?? remote;
        }

        return entry with { Label = label, RemotePath = remote, IsReady = ready };
    }

    private static DriveKind? KindOf(DriveType type) => type switch
    {
        DriveType.Fixed or DriveType.Ram => DriveKind.Fixed,
        DriveType.Removable => DriveKind.Removable,
        DriveType.CDRom => DriveKind.Optical,
        DriveType.Network => DriveKind.Network,
        _ => null, // Unknown, NoRootDirectory: nothing to open
    };

    /// <summary><c>c:</c>, <c>C:\</c> → <c>C</c>; null when the text does not start with a drive letter.</summary>
    private static char? LetterOf(string? name)
    {
        string text = (name ?? string.Empty).Trim();
        return text.Length >= 2 && text[1] == ':' && char.IsAsciiLetter(text[0])
            ? char.ToUpperInvariant(text[0])
            : null;
    }

    private static string RootOf(char letter) => $"{letter}:\\";

    /// <summary>
    /// The remembered ("reconnect at sign-in") disk connections that have a drive letter, with the
    /// path each is mapped to. Read from the user's profile: no server is contacted.
    /// </summary>
    private static unsafe List<(char Letter, string Remote)> RememberedConnections()
    {
        var found = new List<(char Letter, string Remote)>();
        try
        {
            HANDLE handle = default;
            uint rc = (uint)PInvoke.WNetOpenEnum(
                NET_RESOURCE_SCOPE.RESOURCE_REMEMBERED,
                NET_RESOURCE_TYPE.RESOURCETYPE_DISK,
                default(WNET_OPEN_ENUM_USAGE), // ignored for this scope
                (NETRESOURCEW*)null,
                &handle);
            if (rc != NoError)
            {
                // No network provider, or nothing remembered.
                return found;
            }

            void* buffer = NativeMemory.Alloc((nuint)EnumBufferBytes);
            try
            {
                for (int call = 0; call < MaxEnumCalls; call++)
                {
                    uint count = uint.MaxValue; // as many entries as fit
                    uint size = EnumBufferBytes;
                    rc = (uint)PInvoke.WNetEnumResource(handle, &count, buffer, &size);
                    if (rc != NoError || count == 0)
                    {
                        // ERROR_NO_MORE_ITEMS ends the list. Anything else (one entry larger than
                        // the buffer included) ends it early: the drive letters are still listed.
                        break;
                    }

                    // The strings the entries point to lie in the same buffer.
                    var entries = (NETRESOURCEW*)buffer;
                    for (uint i = 0; i < count; i++)
                    {
                        char* local = entries[i].lpLocalName.Value;
                        char* remote = entries[i].lpRemoteName.Value;
                        if (local == null || remote == null)
                        {
                            continue; // remembered without a drive letter
                        }

                        string remotePath = new(remote);
                        if (LetterOf(new string(local)) is { } letter && remotePath.Length > 0)
                        {
                            found.Add((letter, remotePath));
                        }
                    }
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
                _ = PInvoke.WNetCloseEnum(handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Warn("DriveCatalog: the remembered network drives could not be listed.", ex);
        }

        return found;
    }

    /// <summary>
    /// <c>Z:\a</c> on a connected network drive as <c>\\server\share\a</c>, else null (a local drive, a
    /// drive not connected). For a process that does not see the user's mapped drives (an elevated one,
    /// WSL). Blocking (network): off the UI thread.
    /// </summary>
    public static string? UncOf(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]) || ConnectionOf(path[0]) is not { } remote)
        {
            return null;
        }

        return PathTools.TrimTrailingSeparators(remote.TrimEnd('\\') + path[2..]);
    }

    /// <summary>
    /// <c>S:\a</c> on a drive made by SUBST as the folder it stands for (<c>E:\work\a</c>), else null (a
    /// volume, a network drive, no such drive). A SUBST drive belongs to the logon session that made it, so
    /// an elevated process does not see it. Resolve before <see cref="UncOf"/> (the folder may be on a
    /// mapped drive).
    /// </summary>
    public static unsafe string? SubstTargetOf(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
        {
            return null;
        }

        try
        {
            string device = $"{path[0]}:";
            char[] buffer = new char[1024];
            uint written;
            fixed (char* name = device)
            fixed (char* target = buffer)
            {
                written = PInvoke.QueryDosDevice(new PCWSTR(name), new PWSTR(target), (uint)buffer.Length);
            }

            if (written == 0)
            {
                return null;
            }

            int end = Array.IndexOf(buffer, '\0');
            string first = new(buffer, 0, end >= 0 ? end : (int)Math.Min(written, (uint)buffer.Length));
            const string Prefix = @"\??\";
            if (!first.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return null; // \Device\HarddiskVolume3, \Device\LanmanRedirector\…: not SUBST
            }

            string mapped = first[Prefix.Length..];
            if (mapped.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                mapped = @"\\" + mapped[4..];
            }

            return PathTools.IsFullyQualified(mapped)
                ? PathTools.TrimTrailingSeparators(mapped.TrimEnd('\\') + path[2..])
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The path a connected network drive is mapped to (<c>\\server\share</c>), or null.</summary>
    private static unsafe string? ConnectionOf(char letter)
    {
        try
        {
            string local = $"{letter}:";
            char[] buffer = new char[1024];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint length = (uint)buffer.Length;
                uint rc;
                fixed (char* localName = local)
                fixed (char* remoteName = buffer)
                {
                    rc = (uint)PInvoke.WNetGetConnection(new PCWSTR(localName), new PWSTR(remoteName), &length);
                }

                if (rc == NoError)
                {
                    int end = Array.IndexOf(buffer, '\0');
                    return end > 0 ? new string(buffer, 0, end) : null;
                }

                if (rc != ErrorMoreData || length <= buffer.Length)
                {
                    // Not connected (ERROR_CONNECTION_UNAVAIL for a remembered drive), not a
                    // network drive, no network.
                    return null;
                }

                buffer = new char[length];
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
