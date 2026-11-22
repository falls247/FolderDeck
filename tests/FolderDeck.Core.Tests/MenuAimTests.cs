using FolderDeck.Core.Input;

namespace FolderDeck.Core.Tests;

public class MenuAimTests
{
    private const long MovingWithin = 100;
    private const double Slack = 4;

    // A child list on the right of the parent: its left edge at x = 300, from y = 100 to 500.
    private static bool HeadsRight(MenuAim aim, long now) =>
        aim.IsHeadingFor(300, 100, 580, 500, targetOnLeft: false, now, MovingWithin, Slack);

    // The same child on the left of the parent: its right edge at x = 100.
    private static bool HeadsLeft(MenuAim aim, long now) =>
        aim.IsHeadingFor(-180, 100, 100, 500, targetOnLeft: true, now, MovingWithin, Slack);

    private static MenuAim Moves(params (double X, double Y, long Time)[] moves)
    {
        var aim = new MenuAim();
        foreach (var (x, y, time) in moves)
        {
            aim.Record(x, y, time);
        }

        return aim;
    }

    [Fact]
    public void DiagonalMove_TowardsAChildOnTheRight_IsHeadingForIt()
    {
        // Down and to the right across the rows below the open one, as on the way to a file far
        // down in the child.
        var aim = Moves((150, 200, 0), (160, 210, 10), (170, 222, 20), (180, 236, 30));
        Assert.True(HeadsRight(aim, now: 60));
        Assert.False(HeadsLeft(aim, now: 60)); // the same move goes away from a child on the left
    }

    [Fact]
    public void Move_TowardsAChildOnTheLeft_IsHeadingForIt()
    {
        var aim = Moves((250, 300, 0), (240, 305, 10), (228, 312, 20));
        Assert.True(HeadsLeft(aim, now: 30));
        Assert.False(HeadsRight(aim, now: 30));
    }

    [Fact]
    public void MovingAway_AlongTheList_OrPastTheChild_IsNotHeadingForIt()
    {
        Assert.False(HeadsRight(Moves((150, 200, 0), (140, 202, 10)), now: 20)); // away (left)
        Assert.False(HeadsRight(Moves((150, 200, 0), (150, 230, 10)), now: 20)); // straight down: another row
        Assert.False(HeadsRight(Moves((150, 200, 0), (151, 160, 10)), now: 20)); // steeply up: passes above the child's top
        Assert.False(HeadsRight(Moves((150, 200, 0), (160, 600, 10)), now: 20)); // below the child
        Assert.False(HeadsRight(Moves((310, 50, 0), (305, 60, 10)), now: 20));   // already beyond its edge (above it)
    }

    [Fact]
    public void NoMove_OrResting_IsNotHeadingForIt()
    {
        Assert.False(HeadsRight(new MenuAim(), now: 0));
        Assert.False(HeadsRight(Moves((150, 200, 0)), now: 10)); // one position: no direction yet

        // The same position again is not a move: the pointer rests since t = 10.
        var resting = Moves((150, 200, 0), (160, 205, 10), (160, 205, 90), (160, 205, 150));
        Assert.True(HeadsRight(resting, now: 100));
        Assert.False(HeadsRight(resting, now: 200)); // resting longer than MovingWithin: decide now

        // Jitter that comes back to where it was gives no heading.
        Assert.False(HeadsRight(Moves((150, 200, 0), (151, 200, 10), (150, 200, 20)), now: 30));

        var cleared = Moves((150, 200, 0), (160, 205, 10));
        cleared.Clear();
        Assert.False(HeadsRight(cleared, now: 20));
    }
}
