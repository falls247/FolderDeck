using System.IO.Compression;
using System.Xml;

namespace FolderDeck.Core.Listing;

/// <summary>A relationship of a package part; <see cref="Target"/> is resolved to a part name.</summary>
internal readonly record struct PartRelationship(string Id, string Type, string Target);

/// <summary>
/// Read-only access to the parts of an Open XML package (docx / xlsx / pptx) for previews.
/// The file is opened shared (it may be open in Office), XML is read without DTDs, and every
/// part read through <see cref="OpenXml"/> counts against one decompressed-bytes budget, so a
/// zip bomb costs at most that many bytes. Methods throw on broken files; callers catch.
/// </summary>
internal sealed class OpenXmlPackage : IDisposable
{
    /// <summary>A package with more parts than this is not a document worth previewing.</summary>
    private const int MaxEntries = 20000;

    private const string PackageRelationships = "_rels/.rels";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly ReadBudget _budget;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private OpenXmlPackage(FileStream file, ZipArchive zip, ReadBudget budget)
    {
        _file = file;
        _zip = zip;
        _budget = budget;
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("Too many parts.");
        }

        // Part names are case-insensitive in Open XML packages.
        foreach (var entry in zip.Entries)
        {
            _entries.TryAdd(entry.FullName.Replace('\\', '/'), entry);
        }
    }

    public static OpenXmlPackage Open(string path, long budgetBytes, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                return new OpenXmlPackage(file, zip, new ReadBudget(budgetBytes, cancellationToken));
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public bool Contains(string part) => _entries.ContainsKey(part);

    /// <summary>An XML reader over a part (null when the part is missing). Dispose it.</summary>
    public XmlReader? OpenXml(string part)
    {
        if (!_entries.TryGetValue(part, out var entry))
        {
            return null;
        }

        _budget.Check();
        var stream = new BudgetStream(entry.Open(), _budget);
        try
        {
            return XmlReader.Create(stream, XmlSettings); // CloseInput: the reader disposes the stream
        }
        catch
        {
            stream.Dispose(); // Create reads the first bytes, which can fail (broken data, budget spent)
            throw;
        }
    }

    /// <summary>The package's main part (<c>word/document.xml</c>, <c>xl/workbook.xml</c>…) per <c>_rels/.rels</c>.</summary>
    public string? MainPart()
    {
        foreach (var rel in RelationshipsOf(string.Empty))
        {
            if (rel.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase) && Contains(rel.Target))
            {
                return rel.Target;
            }
        }

        return null;
    }

    /// <summary>Relationships of <paramref name="part"/> ("" = the package itself); external targets are left out.</summary>
    public List<PartRelationship> RelationshipsOf(string part)
    {
        var result = new List<PartRelationship>();
        using var reader = OpenXml(RelationshipsPartOf(part));
        if (reader is null)
        {
            return result;
        }

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship"
                || string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (reader.GetAttribute("Target") is { Length: > 0 } target)
            {
                result.Add(new PartRelationship(reader.GetAttribute("Id") ?? string.Empty, reader.GetAttribute("Type") ?? string.Empty, ResolveTarget(part, target)));
            }
        }

        return result;
    }

    /// <summary>Target of the first relationship whose type ends with <paramref name="typeSuffix"/>, if that part exists.</summary>
    public string? FindRelated(List<PartRelationship> relationships, string typeSuffix)
    {
        foreach (var rel in relationships)
        {
            if (rel.Type.EndsWith(typeSuffix, StringComparison.OrdinalIgnoreCase) && Contains(rel.Target))
            {
                return rel.Target;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _zip.Dispose();
        _file.Dispose();
    }

    /// <summary><c>xl/workbook.xml</c> → <c>xl/_rels/workbook.xml.rels</c>; "" → <c>_rels/.rels</c>.</summary>
    public static string RelationshipsPartOf(string part)
    {
        if (part.Length == 0)
        {
            return PackageRelationships;
        }

        int slash = part.LastIndexOf('/');
        return part[..(slash + 1)] + "_rels/" + part[(slash + 1)..] + ".rels";
    }

    /// <summary>Resolves a relationship target (relative to the source part's folder, or absolute) to a part name.</summary>
    public static string ResolveTarget(string sourcePart, string target)
    {
        string unescaped = Uri.UnescapeDataString(target).Replace('\\', '/');
        string combined = unescaped.StartsWith('/') ? unescaped : sourcePart[..(sourcePart.LastIndexOf('/') + 1)] + unescaped;
        var segments = new List<string>();
        foreach (string segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>Decompressed bytes left for one preview, shared by every part it reads.</summary>
    private sealed class ReadBudget(long bytes, CancellationToken cancellationToken)
    {
        private long _remaining = bytes;

        public void Check() => cancellationToken.ThrowIfCancellationRequested();

        public void Spend(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _remaining -= count;
            if (_remaining < 0)
            {
                throw new InvalidDataException("The package expands beyond the preview budget.");
            }
        }
    }

    /// <summary>Read-only pass-through stream that charges every byte read to a <see cref="ReadBudget"/>.</summary>
    private sealed class BudgetStream(Stream inner, ReadBudget budget) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            budget.Spend(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
