using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderDeck.Core.Layout;

namespace FolderDeck.ViewModels;

public enum MessageKind
{
    Success,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Message line (§5.4): success and info vanish after 5 s; warnings and errors stay until the next
/// action. Shown at the top or the bottom of the main area by <see cref="Placement"/> (the setting
/// メッセージの表示): the view binds <see cref="ShowsAtTop"/> / <see cref="ShowsAtBottom"/>, never
/// <see cref="IsVisible"/>.
/// </summary>
public sealed partial class MessageBarViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAtTop), nameof(ShowsAtBottom))]
    private MessageKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAtTop), nameof(ShowsAtBottom))]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAtTop), nameof(ShowsAtBottom))]
    private MessagePlacement _placement;

    /// <summary>
    /// The line under the toolbar: for <see cref="MessagePlacement.Top"/>, and for everything but
    /// success while <see cref="MessagePlacement.Off"/>. Info stays because it also carries the
    /// reasons an action was refused (見つかりません, 〜できません, 前の操作が終わってから…): a refusal
    /// is never silent.
    /// </summary>
    public bool ShowsAtTop => IsVisible && (Placement == MessagePlacement.Top
        || (Placement == MessagePlacement.Off && Kind is not MessageKind.Success));

    /// <summary>The line under the items, at the bottom of the main area.</summary>
    public bool ShowsAtBottom => IsVisible && Placement == MessagePlacement.Bottom;

    public MessageBarViewModel()
    {
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            IsVisible = false;
        };
    }

    public void Success(string text) => Show(MessageKind.Success, text);

    public void Info(string text) => Show(MessageKind.Info, text);

    public void Warning(string text) => Show(MessageKind.Warning, text);

    public void Error(string text) => Show(MessageKind.Error, text);

    public void Show(MessageKind kind, string text)
    {
        _timer.Stop();
        Kind = kind;
        Text = text;
        IsVisible = true;
        if (kind is MessageKind.Success or MessageKind.Info)
        {
            _timer.Start();
        }
    }

    /// <summary>Called on the next user action: removes a lingering error or warning.</summary>
    public void ClearPersistent()
    {
        if (IsVisible && Kind is MessageKind.Error or MessageKind.Warning)
        {
            IsVisible = false;
        }
    }

    public void Hide()
    {
        _timer.Stop();
        IsVisible = false;
    }
}
