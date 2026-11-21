using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

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
/// action.
/// </summary>
public sealed partial class MessageBarViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    private MessageKind _kind;

    [ObservableProperty]
    private bool _isVisible;

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
