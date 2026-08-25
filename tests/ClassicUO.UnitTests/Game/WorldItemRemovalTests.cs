using ClassicUO.Game;
using Xunit;

namespace ClassicUO.UnitTests.Game;

public sealed class WorldItemRemovalTests
{
    [Fact]
    public void MatchingAuthoritativeUpdateCancelsPendingRemoval()
    {
        var world = new World
        {
            ObjectToRemove = 0x40000001
        };

        world.CancelPendingItemRemoval(0x40000001);

        Assert.Equal(0u, world.ObjectToRemove);
        world.Clear();
    }

    [Fact]
    public void UnrelatedAuthoritativeUpdatePreservesPendingRemoval()
    {
        var world = new World
        {
            ObjectToRemove = 0x40000001
        };

        world.CancelPendingItemRemoval(0x40000002);

        Assert.Equal(0x40000001u, world.ObjectToRemove);
        world.Clear();
    }
}
