namespace FolderDeck.Core.Input;

/// <summary>
/// "Right button held + left click": the item that is left-clicked while the right button is down
/// is acted on (shown in Explorer) when the right button is released, instead of being opened by the
/// click or getting its right-click menu.
/// <para>
/// A view feeds this the button events it sees and asks what each one means. It deliberately works
/// without mouse capture: a ListBox gives up its capture on every left button release (even a
/// handled one), which silently dropped the armed gesture on list rows. Instead, whatever could
/// leave a stale gesture behind disarms it: a new right press, a left press without the right
/// button, the pointer leaving the view.
/// </para>
/// </summary>
/// <typeparam name="T">What gets armed (a card, a row…).</typeparam>
public sealed class RockerGesture<T>
    where T : class
{
    private T? _armed;

    /// <summary>An item was left-clicked with the right button held; the right button is still awaited.</summary>
    public bool IsArmed => _armed is not null;

    /// <summary>The right button went down: a new press never continues an earlier gesture.</summary>
    public void RightDown() => _armed = null;

    /// <summary>
    /// The left button went down on <paramref name="target"/> (null: not on an item).
    /// Returns true when the press belongs to the gesture; the caller then swallows it (it is
    /// neither a click nor the start of a drag). The first item clicked stays the armed one.
    /// </summary>
    public bool LeftDown(bool rightHeld, T? target)
    {
        if (!rightHeld)
        {
            _armed = null;
            return false;
        }

        _armed ??= target;
        return _armed is not null;
    }

    /// <summary>The left button went up. True while armed: the release belongs to the gesture, not to a click.</summary>
    public bool LeftUp() => _armed is not null;

    /// <summary>
    /// The right button went up. Returns the armed item (the caller acts on it and shows no menu)
    /// and disarms; null when no gesture is under way (an ordinary right click).
    /// </summary>
    public T? RightUp()
    {
        var armed = _armed;
        _armed = null;
        return armed;
    }

    /// <summary>The pointer left the view, or what it shows changed: forget the gesture.</summary>
    public void Cancel() => _armed = null;
}
