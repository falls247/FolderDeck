using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Text;

namespace FolderDeck.ViewModels;

/// <summary>The preview shown next to an image / presentation row of the cascade.</summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    private ImageSource? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
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

    public bool ShowStatus => Image is null;

    public void Show(ImageSource image)
    {
        Image = image;
        IsLoading = false;
    }

    public void Fail()
    {
        Image = null;
        IsLoading = false;
        StatusText = "プレビューを表示できません";
    }
}
