namespace FolderDeck.Core.Listing;

/// <summary>How the preview beside a file row of the overlay / flyouts is made.</summary>
public enum PreviewKind
{
    /// <summary>No preview of its own (the app may still use a shell thumbnail provider).</summary>
    None,

    /// <summary>A picture: the shell thumbnail.</summary>
    Image,

    /// <summary>A PowerPoint presentation: its cover (the first slide).</summary>
    Presentation,

    /// <summary>A Word document: the start of its body text.</summary>
    WordDocument,

    /// <summary>An Excel workbook: the top-left of its first sheet.</summary>
    Spreadsheet,

    /// <summary>A plain-text file (notes, logs, source code…): its first lines.</summary>
    Text,
}

/// <summary>
/// Which file rows of the overlay / flyouts show a preview when the pointer rests on them:
/// images, Open XML presentations / documents / workbooks, and common plain-text files.
/// </summary>
public static class PreviewKinds
{
    private static readonly HashSet<string> ExtraImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jfif", ".tif", ".tiff", ".heic", ".heif", ".avif",
    };

    /// <summary>Open XML presentations: they carry a thumbnail of the first slide (<c>docProps/thumbnail.*</c>).</summary>
    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm",
    };

    /// <summary>Open XML word-processing files (the legacy binary <c>.doc</c> is not read).</summary>
    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm",
    };

    /// <summary>Open XML workbooks (the legacy binary <c>.xls</c> and <c>.xlsb</c> are not read).</summary>
    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xlsm", ".xltx", ".xltm",
    };

    /// <summary>Plain-text formats whose first lines are worth showing.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // notes, logs, data
        ".txt", ".text", ".log", ".md", ".markdown", ".rst", ".adoc", ".csv", ".tsv", ".json", ".jsonc", ".json5", ".ndjson",
        ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".config", ".toml", ".properties", ".env", ".editorconfig",
        ".gitignore", ".gitattributes", ".srt", ".vtt", ".ass", ".diff", ".patch", ".tex", ".bib", ".reg", ".inf",

        // source code and scripts
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hh", ".hxx", ".m", ".mm",
        ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".tsx", ".jsx", ".vue", ".svelte",
        ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle", ".go", ".rs", ".rb", ".php", ".pl", ".pm", ".lua", ".r",
        ".swift", ".dart", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".vbs", ".sql",
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".xaml", ".razor", ".cshtml",
        ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".props", ".targets", ".sln", ".slnx", ".resx", ".manifest",
        ".cmake", ".make", ".mk", ".dockerfile", ".proto", ".graphql", ".gql",
    };

    /// <summary>Video formats Windows draws a frame of (treated like files that have a thumbnail provider).</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".m2ts", ".mts", ".flv",
    };

    /// <summary>Programs and shortcuts: their "thumbnail" is their icon, never a preview.</summary>
    private static readonly HashSet<string> NeverPreviewedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".lnk", ".url", ".msi", ".scr", ".com", ".sys", ".cpl", ".appref-ms",
    };

    public static bool IsImage(string name) =>
        CoverFinder.IsImageName(name) || ExtraImageExtensions.Contains(Path.GetExtension(name));

    public static bool IsPresentation(string name) => PresentationExtensions.Contains(Path.GetExtension(name));

    public static bool IsWordDocument(string name) => WordExtensions.Contains(Path.GetExtension(name));

    public static bool IsSpreadsheet(string name) => SpreadsheetExtensions.Contains(Path.GetExtension(name));

    public static bool IsText(string name) => TextExtensions.Contains(Path.GetExtension(name));

    /// <summary>A presentation, document or workbook: a zip package that may embed a thumbnail.</summary>
    public static bool IsOpenXml(string name) => IsPresentation(name) || IsWordDocument(name) || IsSpreadsheet(name);

    public static bool IsVideo(string name) => VideoExtensions.Contains(Path.GetExtension(name));

    /// <summary>Executables and shortcuts: never previewed, whatever the shell says.</summary>
    public static bool IsNeverPreviewed(string name) => NeverPreviewedExtensions.Contains(Path.GetExtension(name));

    public static PreviewKind KindOf(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext.Length < 2)
        {
            return PreviewKind.None;
        }

        if (IsImage(name))
        {
            return PreviewKind.Image;
        }

        if (PresentationExtensions.Contains(ext))
        {
            return PreviewKind.Presentation;
        }

        if (WordExtensions.Contains(ext))
        {
            return PreviewKind.WordDocument;
        }

        if (SpreadsheetExtensions.Contains(ext))
        {
            return PreviewKind.Spreadsheet;
        }

        return TextExtensions.Contains(ext) ? PreviewKind.Text : PreviewKind.None;
    }

    public static bool IsPreviewable(string name) => KindOf(name) != PreviewKind.None;
}
