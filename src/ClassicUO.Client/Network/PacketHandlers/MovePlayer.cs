using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class MovePlayer
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        var direction = (Direction)p.ReadUInt8();

        if (BeyondRecallQA.BrQaSession.IsActive)
            BeyondRecallQA.BrQaSession.Instance.EmitMovementAcknowledged((direction & Direction.Mask).ToString());

        world.Player.Walk(direction & Direction.Mask, (direction & Direction.Running) != 0);
    }
}
