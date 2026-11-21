using System.Globalization;
using System.Text;
using System.Xml;

namespace FolderDeck.Core.Listing;

/// <summary>Streams the top-left corner of a workbook's first sheet (see <see cref="OfficeText.ReadSheet"/>).</summary>
internal static class SheetReader
{
    private const string FallbackWorkbookPart = "xl/workbook.xml";
    private const string FallbackSheetPart = "xl/worksheets/sheet1.xml";
    private const string FallbackSharedStringsPart = "xl/sharedStrings.xml";
    private const string FallbackStylesPart = "xl/styles.xml";

    /// <summary>Rows past the excerpt that are looked at to tell whether anything follows.</summary>
    private const int TrailingRowsChecked = 256;

    public static SheetExcerpt? Read(OpenXmlPackage package, int maxRows, int maxCols)
    {
        string workbookPart = package.MainPart() ?? FallbackWorkbookPart;
        var workbook = ReadWorkbook(package, workbookPart);
        if (workbook is null || workbook.Sheets.Count == 0)
        {
            return null;
        }

        var relationships = package.RelationshipsOf(workbookPart);
        var (sheetName, sheetPart) = ChooseSheet(package, workbook.Sheets, relationships);
        if (sheetPart is null)
        {
            return null;
        }

        RawCell?[] cells;
        bool truncated;
        using (var reader = package.OpenXml(sheetPart))
        {
            if (reader is null)
            {
                return null;
            }

            (cells, truncated) = ReadCells(reader, maxRows, maxCols);
        }

        var neededStrings = new HashSet<int>();
        int maxStyle = -1;
        foreach (var cell in cells)
        {
            if (cell is not { } c)
            {
                continue;
            }

            if (c.Type == "s" && int.TryParse(c.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                neededStrings.Add(index);
            }
            else if ((c.Type is null or "n") && c.Style > maxStyle)
            {
                maxStyle = c.Style;
            }
        }

        var strings = ReadSharedStrings(package, package.FindRelated(relationships, "/sharedStrings") ?? Existing(package, FallbackSharedStringsPart), neededStrings);
        var styles = maxStyle > 0
            ? ReadStyles(package, package.FindRelated(relationships, "/styles") ?? Existing(package, FallbackStylesPart), maxStyle)
            : StyleTable.Empty;

        var display = new SheetCell[cells.Length];
        int lastRow = -1;
        int lastCol = -1;
        for (int i = 0; i < cells.Length; i++)
        {
            display[i] = cells[i] is { } c ? Display(c, strings, styles, workbook.Date1904) : SheetCell.Empty;
            if (display[i].Text.Length > 0)
            {
                lastRow = Math.Max(lastRow, i / maxCols);
                lastCol = Math.Max(lastCol, i % maxCols);
            }
        }

        var rows = new List<IReadOnlyList<SheetCell>>(lastRow + 1);
        for (int r = 0; r <= lastRow; r++)
        {
            var row = new SheetCell[lastCol + 1];
            Array.Copy(display, r * maxCols, row, 0, lastCol + 1);
            rows.Add(row);
        }

        return new SheetExcerpt(sheetName, workbook.Sheets.Count, rows, lastCol + 1, truncated);
    }

    private static string? Existing(OpenXmlPackage package, string part) => package.Contains(part) ? part : null;

    // ---- workbook ---------------------------------------------------------------------------

    private static Workbook? ReadWorkbook(OpenXmlPackage package, string part)
    {
        using var reader = package.OpenXml(part);
        if (reader is null)
        {
            return null;
        }

        var sheets = new List<SheetEntry>();
        bool date1904 = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "workbookPr":
                    date1904 = Attribute(reader, "date1904") is { } flag && (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase));
                    break;
                case "sheet":
                    string state = Attribute(reader, "state") ?? string.Empty;
                    sheets.Add(new SheetEntry(
                        Attribute(reader, "name") ?? string.Empty,
                        Attribute(reader, "id"), // r:id (sheetId has another local name)
                        state.Equals("hidden", StringComparison.OrdinalIgnoreCase) || state.Equals("veryHidden", StringComparison.OrdinalIgnoreCase)));
                    break;
                case "calcPr" or "definedNames" or "extLst":
                    return new Workbook(date1904, sheets); // after <sheets>: nothing more to learn
            }
        }

        return new Workbook(date1904, sheets);
    }

    /// <summary>The first visible worksheet in workbook order (chart sheets have no cells), else the first worksheet.</summary>
    private static (string Name, string? Part) ChooseSheet(OpenXmlPackage package, List<SheetEntry> sheets, List<PartRelationship> relationships)
    {
        string? PartOf(SheetEntry sheet)
        {
            foreach (var rel in relationships)
            {
                if (rel.Id == sheet.RelationshipId && rel.Type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase) && package.Contains(rel.Target))
                {
                    return rel.Target;
                }
            }

            return null;
        }

        foreach (bool allowHidden in new[] { false, true })
        {
            foreach (var sheet in sheets)
            {
                if ((allowHidden || !sheet.Hidden) && PartOf(sheet) is { } part)
                {
                    return (sheet.Name, part);
                }
            }
        }

        return relationships.Count == 0 && package.Contains(FallbackSheetPart) ? (sheets[0].Name, FallbackSheetPart) : (string.Empty, null);
    }

    // ---- cells ------------------------------------------------------------------------------

    /// <summary>Reads rows 1…<paramref name="maxRows"/> into a dense <c>maxRows × maxCols</c> array, then stops.</summary>
    private static (RawCell?[] Cells, bool Truncated) ReadCells(XmlReader reader, int maxRows, int maxCols)
    {
        var cells = new RawCell?[checked(maxRows * maxCols)];
        bool truncated = false;
        bool inData = false;
        int rowNumber = 0;
        int rowsPast = 0;

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheetData")
            {
                if (reader.IsEmptyElement)
                {
                    break;
                }

                inData = true;
            }
            else if (inData && reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                rowNumber = ParsePositive(Attribute(reader, "r")) ?? Next(rowNumber);
                if (rowNumber > maxRows)
                {
                    // Formatting-only rows (empty elements, or cells with a style but no value) often
                    // follow the data: look a little further.
                    if (!reader.IsEmptyElement && RowHasValue(reader))
                    {
                        truncated = true;
                        break;
                    }

                    if (++rowsPast > TrailingRowsChecked)
                    {
                        break;
                    }
                }
                else if (!reader.IsEmptyElement)
                {
                    ReadRow(reader, rowNumber, cells, maxRows, maxCols, ref truncated);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "sheetData")
            {
                break;
            }

            reader.Read();
        }

        return (cells, truncated);
    }

    /// <summary>Reads the cells of a row; leaves the reader on its end tag.</summary>
    private static void ReadRow(XmlReader reader, int row, RawCell?[] cells, int maxRows, int maxCols, ref bool truncated)
    {
        int depth = reader.Depth;
        int column = 0;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName != "c")
                {
                    reader.Skip();
                    continue;
                }

                column = ColumnOf(Attribute(reader, "r")) ?? Next(column);
                string? type = Attribute(reader, "t");
                int style = ParsePositive(Attribute(reader, "s")) ?? 0;
                string? value = reader.IsEmptyElement ? null : ReadCellValue(reader);
                if (column > maxCols)
                {
                    truncated |= !string.IsNullOrEmpty(value);
                }
                else if (value is not null && row >= 1 && row <= maxRows && column >= 1)
                {
                    cells[((row - 1) * maxCols) + (column - 1)] = new RawCell(type, value, style);
                }
            }

            reader.Read();
        }
    }

    /// <summary>
    /// Whether a row past the excerpt holds a cell with a value (not just a style). Leaves the reader
    /// on the row's end tag when it does not.
    /// </summary>
    private static bool RowHasValue(XmlReader reader)
    {
        int depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName != "c")
                {
                    reader.Skip();
                    continue;
                }

                if (!reader.IsEmptyElement && !string.IsNullOrEmpty(ReadCellValue(reader)))
                {
                    return true;
                }
            }

            reader.Read();
        }

        return false;
    }

    /// <summary>The row / column after <paramref name="number"/>, saturating instead of wrapping past <see cref="int.MaxValue"/>.</summary>
    private static int Next(int number) => number < int.MaxValue ? number + 1 : int.MaxValue;

    /// <summary>The <c>&lt;v&gt;</c> or inline string of a cell; leaves the reader on the cell's end tag.</summary>
    private static string? ReadCellValue(XmlReader reader)
    {
        int depth = reader.Depth;
        string? value = null;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "v":
                        value = reader.ReadElementContentAsString();
                        continue;
                    case "is":
                        value = ReadRichText(reader);
                        continue;
                    default:
                        reader.Skip(); // formula
                        continue;
                }
            }

            reader.Read();
        }

        return value;
    }

    /// <summary>
    /// Text of a shared / inline string (<c>&lt;si&gt;</c>, <c>&lt;is&gt;</c>): its runs joined, phonetic guides
    /// (furigana) left out. Moves the reader past the element.
    /// </summary>
    private static string ReadRichText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        int depth = reader.Depth;
        var text = new StringBuilder();
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "rPh" or "phoneticPr" or "rPr":
                        reader.Skip();
                        continue;
                    case "t":
                        string part = reader.ReadElementContentAsString();
                        if (text.Length <= OfficeText.MaxCellChars)
                        {
                            text.Append(part);
                        }

                        continue;
                }
            }

            reader.Read();
        }

        reader.Read();
        return text.ToString();
    }

    private static Dictionary<int, string> ReadSharedStrings(OpenXmlPackage package, string? part, HashSet<int> needed)
    {
        var result = new Dictionary<int, string>();
        if (part is null || needed.Count == 0)
        {
            return result;
        }

        int last = needed.Max();
        try
        {
            using var reader = package.OpenXml(part);
            if (reader is null)
            {
                return result;
            }

            int index = -1;
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    index++;
                    if (needed.Contains(index))
                    {
                        result[index] = ReadRichText(reader);
                    }
                    else
                    {
                        reader.Skip();
                    }

                    if (index >= last)
                    {
                        break;
                    }

                    continue;
                }

                reader.Read();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            // Keep the strings read before the part turned out broken (or too big).
        }

        return result;
    }

    private static StyleTable ReadStyles(OpenXmlPackage package, string? part, int maxStyle)
    {
        var custom = new Dictionary<int, string>();
        var formats = new List<int>();
        if (part is null)
        {
            return StyleTable.Empty;
        }

        try
        {
            using var reader = package.OpenXml(part);
            if (reader is null)
            {
                return StyleTable.Empty;
            }

            int cellXfsDepth = -1;
            while (reader.Read() && formats.Count <= maxStyle)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "numFmt" when cellXfsDepth < 0:
                            if (ParsePositive(Attribute(reader, "numFmtId"), allowZero: true) is { } id)
                            {
                                custom[id] = Attribute(reader, "formatCode") ?? string.Empty;
                            }

                            break;
                        case "cellXfs" when !reader.IsEmptyElement:
                            cellXfsDepth = reader.Depth;
                            break;
                        case "xf" when cellXfsDepth >= 0 && reader.Depth == cellXfsDepth + 1:
                            formats.Add(ParsePositive(Attribute(reader, "numFmtId"), allowZero: true) ?? 0);
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellXfsDepth && reader.LocalName == "cellXfs")
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            // Keep the formats read so far.
        }

        return new StyleTable(custom, formats);
    }

    private static SheetCell Display(RawCell cell, Dictionary<int, string> strings, StyleTable styles, bool date1904)
    {
        string value = cell.Value;
        switch (cell.Type)
        {
            case "s":
                return Text(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && strings.TryGetValue(index, out var shared) ? shared : string.Empty);
            case "inlineStr" or "str" or "e":
                return Text(value);
            case "b":
                return new SheetCell(value.Trim() is "1" or "true" ? "TRUE" : "FALSE", false);
            case "d":
                return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var iso)
                    ? new SheetCell(iso.ToString(iso.TimeOfDay == TimeSpan.Zero ? "yyyy/MM/dd" : "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture), true)
                    : Text(value);
        }

        if (value.Length == 0)
        {
            return SheetCell.Empty;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return Text(value);
        }

        var parts = styles.DatePartsOf(cell.Style);
        if (parts != DateParts.None && SheetFormat.FormatDate(number, parts, date1904) is { } date)
        {
            return new SheetCell(date, true);
        }

        return new SheetCell(SheetFormat.General(number), true);
    }

    /// <summary>One line of text: <c>_xHHHH_</c> escapes decoded, line breaks / tabs as spaces, long text cut.</summary>
    private static SheetCell Text(string value)
    {
        string decoded = SheetFormat.DecodeEscapes(value);
        var text = new StringBuilder(Math.Min(decoded.Length, OfficeText.MaxCellChars + 1));
        bool space = false;
        foreach (char c in decoded)
        {
            if (text.Length >= OfficeText.MaxCellChars)
            {
                text.Append('…');
                break;
            }

            if (char.IsControl(c))
            {
                if (!space && text.Length > 0)
                {
                    text.Append(' ');
                }

                space = true;
                continue;
            }

            text.Append(c);
            space = false;
        }

        return new SheetCell(text.ToString().TrimEnd(), false);
    }

    /// <summary>1-based column of an A1 reference ("C7" → 3), or null.</summary>
    internal static int? ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        int column = 0;
        int i = 0;
        for (; i < reference.Length && column <= 1_000_000; i++)
        {
            char c = char.ToUpperInvariant(reference[i]);
            if (c is < 'A' or > 'Z')
            {
                break;
            }

            column = (column * 26) + (c - 'A' + 1);
        }

        return i == 0 || column > 1_000_000 ? null : column;
    }

    private static int? ParsePositive(string? text, bool allowZero = false) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && (value > 0 || (allowZero && value == 0)) ? value : null;

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

    private readonly record struct RawCell(string? Type, string Value, int Style);

    private sealed record SheetEntry(string Name, string? RelationshipId, bool Hidden);

    private sealed record Workbook(bool Date1904, List<SheetEntry> Sheets);

    /// <summary>Number format of each cell style (<c>cellXfs</c>) and the workbook's custom formats.</summary>
    private sealed class StyleTable(Dictionary<int, string> custom, List<int> formats)
    {
        public static readonly StyleTable Empty = new(new Dictionary<int, string>(), new List<int>());

        public DateParts DatePartsOf(int style)
        {
            int id = style >= 0 && style < formats.Count ? formats[style] : 0;
            return custom.TryGetValue(id, out string? code) ? SheetFormat.DatePartsOf(code) : SheetFormat.BuiltInDateParts(id);
        }
    }
}

/// <summary>What a date / time number format shows.</summary>
[Flags]
internal enum DateParts
{
    None = 0,
    Date = 1,
    Time = 2,
    Seconds = 4,

    /// <summary>Elapsed time (<c>[h]:mm</c>): hours past 24 are kept.</summary>
    Elapsed = 8,
}

/// <summary>Display rules for sheet values: Excel's "General" look and simple dates.</summary>
internal static class SheetFormat
{
    /// <summary>Serial 2958466 is 10000-01-01, past what dates can show.</summary>
    private const double MaxSerial = 2958466;

    /// <summary>Days from 1900-01-00 to 1904-01-01 (the 1904 date system).</summary>
    private const double Offset1904 = 1462;

    /// <summary>Invariant, at most 15 significant digits, no exponent for ordinary magnitudes, no trailing zeros.</summary>
    public static string General(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (value == 0)
        {
            return "0";
        }

        string text = value.ToString("G15", CultureInfo.InvariantCulture);
        double magnitude = Math.Abs(value);
        if (text.Contains('E', StringComparison.Ordinal) && magnitude >= 1e-9 && magnitude < 1e21)
        {
            return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>Built-in number formats that show dates or times (ECMA-376 §18.8.30, with the CJK ones).</summary>
    public static DateParts BuiltInDateParts(int id) => id switch
    {
        14 or 15 or 16 or 17 => DateParts.Date,
        18 or 20 or 32 => DateParts.Time,
        19 or 21 or 33 or 45 or 47 => DateParts.Time | DateParts.Seconds,
        22 => DateParts.Date | DateParts.Time,
        46 => DateParts.Time | DateParts.Seconds | DateParts.Elapsed,
        >= 27 and <= 31 => DateParts.Date,
        >= 34 and <= 36 => DateParts.Date,
        >= 50 and <= 58 => DateParts.Date,
        _ => DateParts.None,
    };

    /// <summary>
    /// What a custom format code shows. Quoted literals, escaped characters, <c>[Red]</c> / <c>[$-411]</c>
    /// sections and the keyword General are ignored; y / d mean a date, h / s a time (m is month or
    /// minute, so it decides nothing alone). Only the first section (positive numbers) counts.
    /// </summary>
    public static DateParts DatePartsOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return DateParts.None;
        }

        var tokens = new StringBuilder(code.Length);
        bool elapsed = false;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c == ';')
            {
                break;
            }

            switch (c)
            {
                case '"':
                    int close = code.IndexOf('"', i + 1);
                    i = close < 0 ? code.Length : close;
                    break;
                case '\\' or '_' or '*':
                    i++; // escaped / padding / fill character
                    break;
                case '[':
                    int end = code.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        i = code.Length;
                        break;
                    }

                    string inner = code[(i + 1)..end];
                    if (inner.Length > 0 && inner.All(ch => ch is 'h' or 'H' or 'm' or 'M' or 's' or 'S'))
                    {
                        elapsed = true;
                        tokens.Append(inner.ToLowerInvariant());
                    }

                    i = end;
                    break;
                default:
                    tokens.Append(char.ToLowerInvariant(c));
                    break;
            }
        }

        string t = tokens.ToString()
            .Replace("general", string.Empty, StringComparison.Ordinal)
            .Replace("am/pm", string.Empty, StringComparison.Ordinal)
            .Replace("a/p", string.Empty, StringComparison.Ordinal);
        bool date = t.Contains('y') || t.Contains('d');
        bool seconds = t.Contains('s');
        bool time = t.Contains('h') || seconds || elapsed;
        if (!date && !time)
        {
            return DateParts.None;
        }

        var parts = DateParts.None;
        if (date)
        {
            parts |= DateParts.Date;
        }

        if (time)
        {
            parts |= DateParts.Time;
        }

        if (seconds)
        {
            parts |= DateParts.Seconds;
        }

        if (elapsed && !date)
        {
            parts |= DateParts.Elapsed;
        }

        return parts;
    }

    /// <summary>A date serial as yyyy/MM/dd, yyyy/MM/dd HH:mm or HH:mm(:ss); null when out of range.</summary>
    public static string? FormatDate(double serial, DateParts parts, bool date1904)
    {
        if (double.IsNaN(serial) || serial < 0 || serial >= MaxSerial)
        {
            return null;
        }

        bool seconds = (parts & DateParts.Seconds) != 0;
        if ((parts & DateParts.Elapsed) != 0)
        {
            long total = (long)Math.Round(serial * 86400, MidpointRounding.AwayFromZero);
            long hours = total / 3600;
            long minutes = total / 60 % 60;
            return seconds
                ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{total % 60:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}");
        }

        // The 1900 system counts a 1900-02-29 that never was (serial 60); OLE dates do not.
        double oa = date1904 ? serial + Offset1904 : serial < 60 ? serial + 1 : serial;
        DateTime value;
        try
        {
            var raw = DateTime.FromOADate(oa);
            value = new DateTime((raw.Ticks + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        }
        catch (ArgumentException)
        {
            return null;
        }

        bool date = (parts & DateParts.Date) != 0;
        bool time = (parts & DateParts.Time) != 0;
        string format = (date, time) switch
        {
            (true, true) => seconds ? "yyyy/MM/dd HH:mm:ss" : "yyyy/MM/dd HH:mm",
            (true, false) => "yyyy/MM/dd",
            _ => seconds ? "HH:mm:ss" : "HH:mm",
        };
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>Decodes the <c>_xHHHH_</c> escapes Excel writes for control characters (<c>_x000D_</c> = CR).</summary>
    public static string DecodeEscapes(string value)
    {
        if (!value.Contains("_x", StringComparison.Ordinal))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '_' && i + 6 < value.Length && value[i + 1] == 'x' && value[i + 6] == '_'
                && int.TryParse(value.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
            {
                text.Append((char)code);
                i += 6;
            }
            else
            {
                text.Append(value[i]);
            }
        }

        return text.ToString();
    }
}
