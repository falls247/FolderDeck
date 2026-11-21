using FolderDeck.Core.Input;

namespace FolderDeck.Core.Tests;

public class RockerGestureTests
{
    [Fact]
    public void RightHeld_LeftClick_FiresOnRightUp_AndSwallowsTheClick()
    {
        var gesture = new RockerGesture<string>();
        gesture.RightDown();
        Assert.True(gesture.LeftDown(rightHeld: true, "card")); // not a click, not a drag
        Assert.True(gesture.LeftUp());

        // The left release must not disarm (a ListBox drops its mouse capture there).
        Assert.True(gesture.IsArmed);
        Assert.Equal("card", gesture.RightUp());
        Assert.Null(gesture.RightUp()); // fired once
    }

    [Fact]
    public void OrdinaryClicks_AreNotTheGesture()
    {
        var gesture = new RockerGesture<string>();
        Assert.False(gesture.LeftDown(rightHeld: false, "card"));
        Assert.False(gesture.LeftUp());

        gesture.RightDown();
        Assert.Null(gesture.RightUp()); // a plain right click: the menu
    }

    [Fact]
    public void LeftClickBesideAnyItem_IsLeftToOthers_UnlessAlreadyArmed()
    {
        var gesture = new RockerGesture<string>();
        Assert.False(gesture.LeftDown(rightHeld: true, null)); // blank space: the card under the list handles it

        Assert.True(gesture.LeftDown(rightHeld: true, "row"));
        Assert.True(gesture.LeftDown(rightHeld: true, null)); // a second click belongs to the same gesture
        Assert.True(gesture.LeftDown(rightHeld: true, "other"));
        Assert.Equal("row", gesture.RightUp()); // the first item clicked
    }

    [Fact]
    public void StaleGesture_IsDroppedByNewPress_OrLeaving()
    {
        var gesture = new RockerGesture<string>();

        // The right release went elsewhere; the next right click must show the menu.
        gesture.LeftDown(rightHeld: true, "card");
        gesture.RightDown();
        Assert.Null(gesture.RightUp());

        // … and a later plain left click must open the item.
        gesture.LeftDown(rightHeld: true, "card");
        Assert.False(gesture.LeftDown(rightHeld: false, "card"));
        Assert.False(gesture.IsArmed);

        gesture.LeftDown(rightHeld: true, "card");
        gesture.Cancel(); // the pointer left
        Assert.Null(gesture.RightUp());
    }
}
