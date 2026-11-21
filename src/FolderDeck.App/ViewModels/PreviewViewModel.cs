using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Text;

namespace FolderDeck.ViewModels;

/// <summary>What the preview card shows below its title.</summary>
public enum PreviewContent
{
    /// <summary>Nothing (yet): the status line says loading, empty or failed.</summary>
    None,

    /// <summary>A picture: the image itself, a shell thumbnail or an embedded cover.</summary>
    Image,

    /// <summary>The first lines of a text file, or the first paragraphs of a Word document.</summary>
    Text,

    /// <summary>The top-left cells of a workbook's first sheet.</summary>
    Table,
}

/// <summary>The preview shown next to a previewable file row of the cascade.</summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus), nameof(ShowDocument))]
    private PreviewContent _content;

    [ObservableProperty]
    private ImageSource? _image;

    /// <summary>Lines of a text file (monospace, not wrapped).</summary>
    [ObservableProperty]
    private IReadOnlyList<string>? _lines;

    /// <summary>Paragraphs of a Word document (wrapped; headings bold).</summary>
    [ObservableProperty]
    private IReadOnlyList<WordParagraph>? _paragraphs;

    [ObservableProperty]
    private SheetExcerpt? _sheet;

    /// <summary>Above a table: "sheet name（ほか N シート）".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCaption))]
    private string _caption = string.Empty;

    /// <summary>The file goes on past what is shown.</summary>
    [ObservableProperty]
    private bool _isTruncated;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusText = "読み込み中…";

    public PreviewViewModel(EntryRowViewModel row)
    {
        Row = row;
        Title = row.Name;
        InfoText = $"{Formatting.FileSize(row.Length)} · {Formatting.DateTimeText(new DateTimeOffset(DateTime.SpecifyKind(row.ModifiedUtc, DateTimeKind.Utc)))}";
    }

    public EntryRowViewModel Row { get; }

    public string Title { get; }

    public string InfoText { get; }

    public string ToolTipText => $"{Row.Path}\nクリックで開く";

    public bool ShowStatus => Content == PreviewContent.None;

    /// <summary>Text or table content (laid out in the clipped document area).</summary>
    public bool ShowDocument => Content is PreviewContent.Text or PreviewContent.Table;

    public bool ShowCaption => Caption.Length > 0;

    public void Show(ImageSource image)
    {
        Image = image;
        IsLoading = false;
        Content = PreviewContent.Image;
    }

    public void ShowLines(TextExcerpt text)
    {
        Lines = text.Lines;
        IsTruncated = text.Truncated;
        IsLoading = false;
        Content = PreviewContent.Text;
    }

    public void ShowParagraphs(WordExcerpt document)
    {
        Paragraphs = document.Paragraphs;
        IsTruncated = document.Truncated;
        IsLoading = false;
        Content = PreviewContent.Text;
    }

    public void ShowSheet(SheetExcerpt sheet)
    {
        Sheet = sheet;
        Caption = sheet.SheetCount > 1 ? $"{sheet.SheetName}（ほか {sheet.SheetCount - 1} シート）" : sheet.SheetName;
        IsTruncated = sheet.Truncated;
        IsLoading = false;
        Content = PreviewContent.Table;
    }

    /// <summary>Nothing to show: says why (by default, that the file cannot be previewed).</summary>
    public void Fail(string? reason = null)
    {
        Image = null;
        IsLoading = false;
        StatusText = reason ?? "プレビューを表示できません";
        Content = PreviewContent.None;
    }
}
