using System.IO.Compression;
using System.Xml;

namespace FolderDeck.Core.Listing;

/// <summary>
/// The thumbnail an Open XML file (pptx, docx, xlsx…) carries inside its zip package —
/// for a presentation, the first slide. PowerPoint saves it by default; it is what lets a
/// card or preview show the cover when no shell thumbnail handler is installed (Office absent).
/// </summary>
public static class OfficeThumbnail
{
    /// <summary>Thumbnails are small (≈256 px); anything bigger is not read.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    private const string RelationshipsPart = "_rels/.rels";
    private const string ThumbnailTypeSuffix = "/metadata/thumbnail";

    private static readonly string[] DecodableExtensions = [".jpeg", ".jpg", ".png", ".gif", ".bmp"];

    private static readonly string[] Fallbacks = ["docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png"];

    /// <summary>The thumbnail's encoded bytes (JPEG / PNG…), or null when there is none or it cannot be read.</summary>
    /// <remarks>
    /// Opens the file shared, so a presentation open in PowerPoint can still be read. A cloud-only
    /// file (<see cref="FileContent.IsCloudOnly(string)"/>) gives null without being opened, so it is
    /// not downloaded. Never throws.
    /// </remarks>
    public static byte[]? ReadEmbedded(string path)
    {
        if (FileContent.IsCloudOnly(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            ZipArchiveEntry? entry = null;
            if (FindThumbnailTarget(zip) is { } target)
            {
                entry = FindEntry(zip, target);
            }

            foreach (string fallback in Fallbacks)
            {
                entry ??= FindEntry(zip, fallback);
            }

            if (entry is null || entry.Length <= 0 || entry.Length > MaxBytes
                || !DecodableExtensions.Contains(Path.GetExtension(entry.FullName), StringComparer.OrdinalIgnoreCase))
            {
                return null; // WMF / EMF thumbnails (old files) are not decodable by WPF
            }

            using var data = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length);
            data.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Target of the package's thumbnail relationship (<c>_rels/.rels</c>), relative to the package root.</summary>
    private static string? FindThumbnailTarget(ZipArchive zip)
    {
        var rels = FindEntry(zip, RelationshipsPart);
        if (rels is null || rels.Length > MaxBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using var relStream = rels.Open(); // the reader does not close it (CloseInput is false)
        using var reader = XmlReader.Create(relStream, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                && reader.GetAttribute("Type") is { } type && type.EndsWith(ThumbnailTypeSuffix, StringComparison.OrdinalIgnoreCase)
                && reader.GetAttribute("Target") is { Length: > 0 } target)
            {
                return Uri.UnescapeDataString(target).TrimStart('/');
            }
        }

        return null;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
    {
        var exact = zip.GetEntry(name);
        if (exact is not null)
        {
            return exact;
        }

        // Part names are case-insensitive in Open XML packages.
        foreach (var entry in zip.Entries)
        {
            if (string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
