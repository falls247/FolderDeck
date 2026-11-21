using System.Text;
using System.Xml;

namespace FolderDeck.Core.Listing;

/// <summary>Streams the body of a word-processing document into paragraphs (see <see cref="OfficeText.ReadWord"/>).</summary>
internal static class WordReader
{
    private const string FallbackDocumentPart = "word/document.xml";
    private const string FallbackStylesPart = "word/styles.xml";

    /// <summary>
    /// Elements whose content is never shown: tracked deletions and property changes, field
    /// instructions, run / table properties, text boxes (and the VML copy of drawings in
    /// <c>mc:Fallback</c>), phonetic guides.
    /// </summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "del", "moveFrom", "delText", "instrText", "delInstrText", "rPr", "sectPr", "txbxContent", "Fallback", "rt",
        "fldData", "tblPr", "tblGrid", "trPr", "tcPr", "pPrChange", "rPrChange", "sectPrChange", "tblPrChange",
        "trPrChange", "tcPrChange", "numberingChange",
    };

    public static WordExcerpt? Read(OpenXmlPackage package, int maxParagraphs, int maxChars)
    {
        string part = package.MainPart() ?? FallbackDocumentPart;
        var collector = new Collector(maxParagraphs, maxChars);
        using (var reader = package.OpenXml(part))
        {
            if (reader is null)
            {
                return null;
            }

            ReadBody(reader, collector);
        }

        var headingStyles = collector.NeedsStyles ? ReadHeadingStyles(package, part) : null;
        return collector.ToExcerpt(headingStyles);
    }

    /// <summary>Style ids that name a heading on their own (English / Japanese built-in ids).</summary>
    public static bool IsHeadingStyleId(string styleId) =>
        styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
        || styleId.StartsWith("見出し", StringComparison.Ordinal)
        || styleId.StartsWith("Title", StringComparison.OrdinalIgnoreCase);

    private static void ReadBody(XmlReader reader, Collector output)
    {
        StringBuilder? text = null; // the open paragraph
        int paragraphDepth = -1;
        int propertiesDepth = -1; // inside the open paragraph's w:pPr
        string? styleId = null;
        bool outline = false;
        var fields = new Stack<bool>(); // complex fields: true while in the instruction part
        int instructions = 0;

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                string name = reader.LocalName;
                if (Skipped.Contains(name))
                {
                    reader.Skip();
                    continue;
                }

                bool inRunText = text is not null && propertiesDepth < 0 && instructions == 0;
                switch (name)
                {
                    case "p" when text is null:
                        if (reader.IsEmptyElement)
                        {
                            if (!output.Add(string.Empty, null, false))
                            {
                                return;
                            }
                        }
                        else
                        {
                            text = new StringBuilder();
                            paragraphDepth = reader.Depth;
                            styleId = null;
                            outline = false;
                        }

                        break;
                    case "pPr" when text is not null && propertiesDepth < 0 && !reader.IsEmptyElement:
                        propertiesDepth = reader.Depth;
                        break;
                    case "pStyle" when propertiesDepth >= 0:
                        styleId = Attribute(reader, "val");
                        break;
                    case "outlineLvl" when propertiesDepth >= 0:
                        outline = IsOutlineHeading(Attribute(reader, "val"));
                        break;
                    case "t":
                        string content = reader.ReadElementContentAsString();
                        if (inRunText)
                        {
                            text!.Append(content);
                        }

                        continue;
                    case "tab" or "ptab" when inRunText:
                        text!.Append('\t');
                        break;
                    case "br" or "cr" when inRunText:
                        text!.Append('\n');
                        break;
                    case "noBreakHyphen" when inRunText:
                        text!.Append('-');
                        break;
                    case "fldChar":
                        switch (Attribute(reader, "fldCharType"))
                        {
                            case "begin":
                                fields.Push(true);
                                instructions++;
                                break;
                            case "separate" when fields.Count > 0 && fields.Peek():
                                fields.Pop();
                                fields.Push(false);
                                instructions--;
                                break;
                            case "end" when fields.Count > 0:
                                if (fields.Pop())
                                {
                                    instructions--;
                                }

                                break;
                        }

                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == propertiesDepth && reader.LocalName == "pPr")
                {
                    propertiesDepth = -1;
                }
                else if (text is not null && reader.Depth == paragraphDepth && reader.LocalName == "p")
                {
                    if (!output.Add(text.ToString(), styleId, outline))
                    {
                        return;
                    }

                    text = null;
                    paragraphDepth = -1;
                }
            }

            reader.Read();
        }
    }

    /// <summary>Paragraph style ids that are headings: by name ("heading 1", "Title"…), outline level, or inheritance.</summary>
    private static HashSet<string>? ReadHeadingStyles(OpenXmlPackage package, string documentPart)
    {
        string? part = package.FindRelated(package.RelationshipsOf(documentPart), "/styles")
            ?? (package.Contains(FallbackStylesPart) ? FallbackStylesPart : null);
        if (part is null)
        {
            return null;
        }

        var styles = new Dictionary<string, (bool Heading, string? BasedOn)>(StringComparer.Ordinal);
        try
        {
            using var reader = package.OpenXml(part);
            if (reader is null)
            {
                return null;
            }

            string? id = null;
            int styleDepth = -1;
            bool heading = false;
            string? basedOn = null;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "style" when !reader.IsEmptyElement:
                            id = Attribute(reader, "type") is null or "paragraph" ? Attribute(reader, "styleId") : null;
                            styleDepth = reader.Depth;
                            heading = false;
                            basedOn = null;
                            break;
                        case "name" when id is not null && reader.Depth == styleDepth + 1:
                            heading |= IsHeadingStyleName(Attribute(reader, "val"));
                            break;
                        case "basedOn" when id is not null && reader.Depth == styleDepth + 1:
                            basedOn = Attribute(reader, "val");
                            break;
                        case "outlineLvl" when id is not null:
                            heading |= IsOutlineHeading(Attribute(reader, "val"));
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == styleDepth && reader.LocalName == "style")
                {
                    if (id is not null)
                    {
                        styles[id] = (heading, basedOn);
                    }

                    id = null;
                    styleDepth = -1;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            // Use what was read before the part turned out broken.
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in styles.Keys)
        {
            string? current = id;
            for (int hops = 0; current is not null && hops < 10 && styles.TryGetValue(current, out var style); hops++)
            {
                if (style.Heading)
                {
                    result.Add(id);
                    break;
                }

                current = style.BasedOn;
            }
        }

        return result;
    }

    private static bool IsHeadingStyleName(string? name) =>
        name is not null
        && (name.StartsWith("heading", StringComparison.OrdinalIgnoreCase)
            || name.Equals("title", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("見出し", StringComparison.Ordinal)
            || name == "表題");

    /// <summary>Outline levels 0–8 are headings; 9 is body text.</summary>
    private static bool IsOutlineHeading(string? value) => int.TryParse(value, out int level) && level is >= 0 and < 9;

    /// <summary>An attribute by local name, whatever its prefix (<c>w:val</c>, <c>val</c>…).</summary>
    private static string? Attribute(XmlReader reader, string localName)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return null;
        }

        string? value = null;
        do
        {
            if (reader.LocalName == localName)
            {
                value = reader.Value;
                break;
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
        return value;
    }

    /// <summary>Collects paragraphs up to the limits; blank paragraphs between text collapse to one.</summary>
    private sealed class Collector(int maxParagraphs, int maxChars)
    {
        private readonly List<(string Text, string? StyleId, bool Outline)> _items = [];
        private int _chars;
        private bool _blankPending;
        private bool _truncated;

        /// <summary>Some style id needs styles.xml to tell whether it is a heading.</summary>
        public bool NeedsStyles
        {
            get
            {
                foreach (var item in _items)
                {
                    if (!item.Outline && item.StyleId is { } id && !IsHeadingStyleId(id))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Adds a finished paragraph; false = the excerpt is full (stop reading).</summary>
        public bool Add(string text, string? styleId, bool outline)
        {
            string trimmed = text.TrimEnd();
            if (trimmed.Trim().Length == 0)
            {
                _blankPending = _items.Count > 0;
                return true;
            }

            int needed = _blankPending ? 2 : 1;
            if (_items.Count + needed > maxParagraphs || _chars >= maxChars)
            {
                _truncated = true;
                return false;
            }

            if (_blankPending)
            {
                _items.Add((string.Empty, null, false));
                _blankPending = false;
            }

            if (_chars + trimmed.Length > maxChars)
            {
                int keep = maxChars - _chars;
                if (keep > 0 && char.IsHighSurrogate(trimmed[keep - 1]))
                {
                    keep--;
                }

                if (keep > 0)
                {
                    _items.Add((trimmed[..keep], styleId, outline));
                }

                _chars = maxChars;
                _truncated = true;
                return false;
            }

            _items.Add((trimmed, styleId, outline));
            _chars += trimmed.Length;
            return true;
        }

        public WordExcerpt ToExcerpt(HashSet<string>? headingStyles)
        {
            var paragraphs = new List<WordParagraph>(_items.Count);
            foreach (var (text, styleId, outline) in _items)
            {
                bool heading = outline
                    || (styleId is not null && (IsHeadingStyleId(styleId) || headingStyles?.Contains(styleId) == true));
                paragraphs.Add(new WordParagraph(text, heading));
            }

            // A blank separator never ends the excerpt.
            if (paragraphs.Count > 0 && paragraphs[^1].Text.Length == 0)
            {
                paragraphs.RemoveAt(paragraphs.Count - 1);
            }

            return new WordExcerpt(paragraphs, _truncated);
        }
    }
}
