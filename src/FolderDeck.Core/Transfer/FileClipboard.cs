using System.Buffers.Binary;
using System.Text;

namespace FolderDeck.Core.Transfer;

/// <summary>Files on the clipboard and whether pasting them copies or moves (a cut).</summary>
public sealed record ClipboardFiles(IReadOnlyList<string> Paths, TransferKind Kind);

/// <summary>
/// Byte layouts of the two clipboard formats Explorer uses for files: CF_HDROP (the file list) and
/// "Preferred DropEffect" (copy or cut). The Win32 calls are in the App project; this is the pure
/// encoding and decoding, which never throws on foreign data.
/// </summary>
public static class FileClipboard
{
    /// <summary>Name of the registered clipboard format that tells a cut from a copy.</summary>
    public const string PreferredDropEffect = "Preferred DropEffect";

    private const uint EffectCopy = 1; // DROPEFFECT_COPY
    private const uint EffectMove = 2; // DROPEFFECT_MOVE
    private const uint EffectLink = 4; // DROPEFFECT_LINK

    /// <summary>sizeof(DROPFILES): pFiles (4), pt (8), fNC (4), fWide (4).</summary>
    private const int HeaderSize = 20;

    private const int WideFlagOffset = 16;

    // The ANSI form of CF_HDROP is in the system code page (932 on a Japanese system). With the
    // provider registered, code page 0 means that page; it is UTF-8 when the system page is UTF-8.
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(0);
    });

    /// <summary>
    /// The "Preferred DropEffect" value: <c>02 00 00 00</c> for a cut, <c>05 00 00 00</c>
    /// (copy | link, as Explorer writes it) for a copy.
    /// </summary>
    public static byte[] EncodeDropEffect(TransferKind kind)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, kind == TransferKind.Move ? EffectMove : EffectCopy | EffectLink);
        return bytes;
    }

    /// <summary>
    /// Reads a "Preferred DropEffect" value. A move only when the move bit is set and the copy bit
    /// is clear; anything else, including a missing or short value, is a copy (the safe reading:
    /// the source stays).
    /// </summary>
    public static TransferKind DecodeDropEffect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return TransferKind.Copy;
        }

        uint effect = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return (effect & EffectMove) != 0 && (effect & EffectCopy) == 0 ? TransferKind.Move : TransferKind.Copy;
    }

    /// <summary>
    /// CF_HDROP bytes: a DROPFILES header (pFiles = 20, fWide = 1) followed by the paths as UTF-16
    /// strings, each ended by a NUL, and one more NUL after the last. Empty paths and paths that
    /// contain a NUL are left out (either would cut the list short for the reader).
    /// </summary>
    public static byte[] EncodeHDrop(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        int chars = 0;
        foreach (string path in paths)
        {
            if (IsEncodable(path))
            {
                chars += path.Length + 1;
            }
        }

        // The list always ends with two NULs, also when it is empty.
        chars += chars == 0 ? 2 : 1;

        byte[] bytes = new byte[HeaderSize + (chars * 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(WideFlagOffset), 1);

        int pos = HeaderSize;
        foreach (string path in paths)
        {
            if (!IsEncodable(path))
            {
                continue;
            }

            foreach (char c in path)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pos), c);
                pos += 2;
            }

            pos += 2; // the NUL after the path (the array is zero-filled)
        }

        return bytes;
    }

    /// <summary>
    /// The paths of a CF_HDROP block, wide (UTF-16) or ANSI (system code page). Malformed data gives
    /// an empty list: a block shorter than the header, a list offset outside the block, or a name
    /// that is cut off before its NUL (a truncated name could be the name of another file). Never
    /// throws.
    /// </summary>
    public static IReadOnlyList<string> DecodeHDrop(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return [];
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (offset < HeaderSize || offset >= (uint)data.Length)
        {
            return [];
        }

        bool wide = BinaryPrimitives.ReadInt32LittleEndian(data[WideFlagOffset..]) != 0;
        ReadOnlySpan<byte> list = data[(int)offset..];
        try
        {
            return wide ? DecodeWide(list) : DecodeAnsi(list);
        }
        catch (Exception)
        {
            // The contract is "never throws": the bytes come from another process.
            return [];
        }
    }

    private static bool IsEncodable(string? path) => !string.IsNullOrEmpty(path) && !path.Contains('\0');

    private static IReadOnlyList<string> DecodeWide(ReadOnlySpan<byte> list)
    {
        var result = new List<string>();
        int pos = 0;
        while (pos + 2 <= list.Length)
        {
            int start = pos;
            bool terminated = false;
            while (pos + 2 <= list.Length)
            {
                ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(list[pos..]);
                pos += 2;
                if (unit == 0)
                {
                    terminated = true;
                    break;
                }
            }

            if (!terminated)
            {
                return []; // cut off inside a name
            }

            int count = (pos - 2 - start) / 2;
            if (count == 0)
            {
                return result; // the empty string ends the list
            }

            // Unit by unit, so that a lone surrogate (legal in an NTFS name) survives.
            char[] chars = new char[count];
            for (int i = 0; i < count; i++)
            {
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(list[(start + (2 * i))..]);
            }

            result.Add(new string(chars));
        }

        // The block ended right after a complete name, without the final NUL: every name is whole.
        return result;
    }

    private static IReadOnlyList<string> DecodeAnsi(ReadOnlySpan<byte> list)
    {
        var result = new List<string>();
        Encoding encoding = Ansi.Value;
        int pos = 0;
        while (pos < list.Length)
        {
            int length = list[pos..].IndexOf((byte)0);
            if (length < 0)
            {
                return []; // cut off inside a name
            }

            if (length == 0)
            {
                return result; // the empty string ends the list
            }

            result.Add(encoding.GetString(list.Slice(pos, length)));
            pos += length + 1;
        }

        return result;
    }
}
