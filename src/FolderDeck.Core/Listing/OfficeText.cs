using System.Xml;

namespace FolderDeck.Core.Listing;

/// <summary>One paragraph of a Word document's body.</summary>
/// <param name="Text">Plain text; tabs and line breaks are kept as <c>\t</c> and <c>\n</c>. Empty for the blank line between paragraphs.</param>
/// <param name="IsHeading">A heading or title (by its style or outline level).</param>
public sealed record WordParagraph(string Text, bool IsHeading);

/// <summary>The first paragraphs of a Word document (see <see cref="OfficeText.ReadWord"/>).</summary>
/// <param name="Truncated">More text follows the excerpt.</param>
public sealed record WordExcerpt(IReadOnlyList<WordParagraph> Paragraphs, bool Truncated)
{
    public bool IsEmpty => Paragraphs.Count == 0;
}

/// <summary>A cell of a sheet excerpt as displayed; <see cref="IsNumber"/> for numbers and dates (shown right aligned).</summary>
public readonly record struct SheetCell(string Text, bool IsNumber)
{
    public static readonly SheetCell Empty = new(string.Empty, false);
}

/// <summary>The top-left corner of a workbook's first sheet (see <see cref="OfficeText.ReadSheet"/>).</summary>
/// <param name="SheetName">Name of the sheet shown.</param>
/// <param name="SheetCount">Number of sheets in the workbook (including the one shown).</param>
/// <param name="Rows">Dense rows starting at A1, each <paramref name="ColumnCount"/> cells wide; trailing empty rows and columns are trimmed.</param>
/// <param name="Truncated">The sheet has content beyond the rows / columns read.</param>
public sealed record SheetExcerpt(string SheetName, int SheetCount, IReadOnlyList<IReadOnlyList<SheetCell>> Rows, int ColumnCount, bool Truncated)
{
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Text of a cell by zero-based position ("" outside the excerpt).</summary>
    public string TextAt(int row, int column) =>
        row >= 0 && row < Rows.Count && column >= 0 && column < ColumnCount ? Rows[row][column].Text : string.Empty;

    /// <summary>Column letters of a zero-based column index: 0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnName(int index)
    {
        var letters = new Stack<char>();
        int n = Math.Max(0, index) + 1;
        while (n > 0)
        {
            n--;
            letters.Push((char)('A' + (n % 26)));
            n /= 26;
        }

        return new string(letters.ToArray());
    }
}

/// <summary>
/// Text excerpts of Open XML documents for the hover preview: the start of a Word document's
/// body and the top-left of a workbook's first sheet. Formatting is not reproduced.
/// Files are opened shared (they may be open in Office), only the parts needed are read, and
/// at most <see cref="MaxDecompressedBytes"/> are decompressed per call. Never throws:
/// a missing, locked, broken or encrypted file gives null, and so does a cloud-only file
/// (<see cref="FileContent.IsCloudOnly(string)"/>), which is not opened so as not to download it.
/// </summary>
public static class OfficeText
{
    /// <summary>Upper bound of decompressed bytes read per call (zip bombs).</summary>
    public const long MaxDecompressedBytes = 32L * 1024 * 1024;

    /// <summary>A cell's text is cut at this length.</summary>
    public const int MaxCellChars = 256;

    /// <summary>
    /// The first paragraphs of a .docx / .docm / .dotx / .dotm: at most <paramref name="maxParagraphs"/>
    /// paragraphs and <paramref name="maxChars"/> characters. Deleted text (tracked changes), field
    /// codes, text boxes and phonetic guides are left out; runs of blank paragraphs become one.
    /// </summary>
    public static WordExcerpt? ReadWord(string path, int maxParagraphs = 40, int maxChars = 4000, CancellationToken cancellationToken = default)
    {
        if (FileContent.IsCloudOnly(path))
        {
            return null;
        }

        try
        {
            using var package = OpenXmlPackage.Open(path, MaxDecompressedBytes, cancellationToken);
            return WordReader.Read(package, Math.Max(1, maxParagraphs), Math.Max(1, maxChars));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The top-left <paramref name="maxRows"/> × <paramref name="maxCols"/> cells of a workbook's first
    /// sheet (.xlsx / .xlsm / .xltx / .xltm) as display strings: numbers in a plain general format,
    /// dates as yyyy/MM/dd (and HH:mm), booleans as TRUE / FALSE. The sheet is streamed and reading
    /// stops after <paramref name="maxRows"/> rows.
    /// </summary>
    public static SheetExcerpt? ReadSheet(string path, int maxRows = 30, int maxCols = 12, CancellationToken cancellationToken = default)
    {
        if (FileContent.IsCloudOnly(path))
        {
            return null;
        }

        try
        {
            using var package = OpenXmlPackage.Open(path, MaxDecompressedBytes, cancellationToken);
            return SheetReader.Read(package, Math.Max(1, maxRows), Math.Max(1, maxCols));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// What a broken or hostile file can make the readers throw. Index and arithmetic errors are
    /// included so that a value no check foresaw still gives null rather than an exception.
    /// </summary>
    internal static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or ArgumentException
            or NotSupportedException or OperationCanceledException or FormatException or ArithmeticException
            or IndexOutOfRangeException or InvalidOperationException or System.Security.SecurityException;
}
