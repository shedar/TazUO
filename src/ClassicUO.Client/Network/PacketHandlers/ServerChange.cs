using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

/// <summary>
/// Consumes the legacy server-change advisory sent before the authoritative facet/map
/// update. ModernUO uses this packet when moving between exact-ML facets.
/// </summary>
internal static class ServerChange
{
    public static void Receive(World world, ref StackDataReader p)
    {
        _ = world;
        _ = p.ReadInt16BE();  // x
        _ = p.ReadInt16BE();  // y
        _ = p.ReadInt16BE();  // z
        _ = p.ReadUInt8();    // reserved
        _ = p.ReadInt16BE();  // reserved
        _ = p.ReadInt16BE();  // reserved
        _ = p.ReadInt16BE();  // map width
        _ = p.ReadInt16BE();  // map height
    }
}
