using System.Text;

namespace FolderDeck.Core.Listing;

/// <summary>The first lines of a text file (see <see cref="TextPreview.Read"/>).</summary>
/// <param name="Lines">Lines as shown: tabs expanded, other control characters removed, long lines cut with "…".</param>
/// <param name="Truncated">The file goes on past the excerpt.</param>
/// <param name="EncodingName">How the bytes were decoded: UTF-8, UTF-16 LE / BE or Shift_JIS.</param>
public sealed record TextExcerpt(IReadOnlyList<string> Lines, bool Truncated, string EncodingName)
{
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Reads the start of a plain-text file for the hover preview. Only the first bytes are read;
/// the encoding is the BOM's (UTF-8 / UTF-16), else UTF-8 when the bytes are valid UTF-8, else
/// Shift_JIS (code page 932). Binary content gives null, and so does a cloud-only file
/// (<see cref="FileContent.IsCloudOnly(string)"/>), which is not opened so as not to download it.
/// Never throws.
/// </summary>
public static class TextPreview
{
    /// <summary>Lines longer than this are cut and end with "…".</summary>
    public const int MaxLineChars = 200;

    private const int TabWidth = 4;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];

    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> ShiftJis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    });

    /// <summary>
    /// At most <paramref name="maxLines"/> lines from the first <paramref name="maxBytes"/> bytes of the
    /// file, or null when it is missing, unreadable or binary.
    /// </summary>
    public static TextExcerpt? Read(string path, int maxLines = 60, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
    {
        if (FileContent.IsCloudOnly(path))
        {
            return null;
        }

        try
        {
            maxBytes = Math.Clamp(maxBytes, 16, Array.MaxLength - 1); // + 1 below must not overflow
            byte[] buffer = new byte[maxBytes + 1]; // one more byte tells whether the file goes on
            int count = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                int read;
                while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
                {
                    count += read;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool cut = count > maxBytes;
            var bytes = new ReadOnlySpan<byte>(buffer, 0, Math.Min(count, maxBytes));
            if (Decode(bytes, cut) is not { } decoded)
            {
                return null;
            }

            var (lines, more) = SplitLines(decoded.Text, Math.Max(1, maxLines));
            return new TextExcerpt(lines, cut || more, decoded.EncodingName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or OperationCanceledException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Decodes the bytes; <paramref name="cut"/> = they stop mid-file, so a split last character is dropped.</summary>
    private static (string Text, string EncodingName)? Decode(ReadOnlySpan<byte> bytes, bool cut)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            var body = bytes[3..];
            return Checked(Encoding.UTF8.GetString(body[..(cut ? Utf8Boundary(body) : body.Length)]), "UTF-8");
        }

        if (bytes.StartsWith(Utf16LeBom))
        {
            return Checked(Utf16(bytes[2..], Encoding.Unicode, bigEndian: false, cut), "UTF-16 LE");
        }

        if (bytes.StartsWith(Utf16BeBom))
        {
            return Checked(Utf16(bytes[2..], Encoding.BigEndianUnicode, bigEndian: true, cut), "UTF-16 BE");
        }

        if (LooksBinary(bytes))
        {
            return null;
        }

        var utf8 = bytes[..(cut ? Utf8Boundary(bytes) : bytes.Length)];
        try
        {
            return (StrictUtf8.GetString(utf8), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // not UTF-8: the usual legacy encoding of Japanese Windows
        }

        var sjis = bytes[..(cut ? ShiftJisBoundary(bytes) : bytes.Length)];
        return (ShiftJis.Value.GetString(sjis), "Shift_JIS");
    }

    private static string Utf16(ReadOnlySpan<byte> body, Encoding encoding, bool bigEndian, bool cut)
    {
        int length = body.Length & ~1;
        if (cut && length >= 2)
        {
            // Drop a high surrogate whose pair was cut off.
            int unit = bigEndian ? (body[length - 2] << 8) | body[length - 1] : body[length - 2] | (body[length - 1] << 8);
            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                length -= 2;
            }
        }

        return encoding.GetString(body[..length]);
    }

    /// <summary>Decoded text with NUL or many control characters is binary.</summary>
    private static (string Text, string EncodingName)? Checked(string text, string encodingName)
    {
        int control = 0;
        foreach (char c in text)
        {
            if (c == '\0')
            {
                return null;
            }

            if (IsSuspiciousControl(c))
            {
                control++;
            }
        }

        return TooManyControls(control, text.Length) ? null : (text, encodingName);
    }

    /// <summary>NUL bytes (UTF-16 has been ruled out) or many control characters.</summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            return true;
        }

        int control = 0;
        foreach (byte b in bytes)
        {
            if (IsSuspiciousControl((char)b))
            {
                control++;
            }
        }

        return TooManyControls(control, bytes.Length);
    }

    /// <summary>C0 controls other than tab, line breaks, form feed and escape (ANSI colors in logs), and DEL.</summary>
    private static bool IsSuspiciousControl(char c) =>
        (c < 0x20 && c is not ('\t' or '\n' or '\r' or '\f' or '\v' or '\x1B')) || c == '\x7F';

    private static bool TooManyControls(int control, int length) => control > Math.Max(4, length / 10);

    /// <summary>Length of <paramref name="bytes"/> without a trailing, incomplete UTF-8 sequence.</summary>
    private static int Utf8Boundary(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length;
        for (int back = 1; back <= Math.Min(4, n); back++)
        {
            byte b = bytes[n - back];
            if ((b & 0xC0) == 0x80)
            {
                continue; // continuation byte
            }

            int needed = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
            return back < needed ? n - back : n;
        }

        return n;
    }

    /// <summary>Length of <paramref name="bytes"/> without a trailing lead byte whose trail byte was cut off.</summary>
    private static int ShiftJisBoundary(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        int last = 0;
        while (i < bytes.Length)
        {
            last = i;
            byte b = bytes[i];
            i += b is (>= 0x81 and <= 0x9F) or (>= 0xE0 and <= 0xFC) ? 2 : 1;
        }

        return i > bytes.Length ? last : bytes.Length;
    }

    private static (List<string> Lines, bool More) SplitLines(string text, int maxLines)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\r' && c != '\n')
            {
                continue;
            }

            if (lines.Count == maxLines)
            {
                return (lines, true);
            }

            lines.Add(Clean(text.AsSpan(start, i - start)));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            if (lines.Count == maxLines)
            {
                return (lines, true);
            }

            lines.Add(Clean(text.AsSpan(start)));
        }

        return (lines, false);
    }

    /// <summary>Tabs → spaces (4-column stops), other control characters removed, cut at <see cref="MaxLineChars"/>.</summary>
    private static string Clean(ReadOnlySpan<char> line)
    {
        var text = new StringBuilder(Math.Min(line.Length, MaxLineChars + 1));
        foreach (char c in line)
        {
            if (text.Length >= MaxLineChars)
            {
                if (char.IsHighSurrogate(text[^1]))
                {
                    text.Length--;
                }

                text.Append('…');
                break;
            }

            if (c == '\t')
            {
                text.Append(' ', TabWidth - (text.Length % TabWidth));
            }
            else if (!char.IsControl(c) && c != '﻿')
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
