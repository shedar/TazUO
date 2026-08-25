using System;
using System.Text;
using ClassicUO.Game;
using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class OpenCompressedGump
{
    public static void Receive(World world, ref StackDataReader p)
    {
        uint sender = p.ReadUInt32BE();
        uint gumpID = p.ReadUInt32BE();
        uint x = p.ReadUInt32BE();
        uint y = p.ReadUInt32BE();
        uint layoutCompressedLen = p.ReadUInt32BE() - 4;
        int layoutDecompressedLen = (int)p.ReadUInt32BE();

        if (layoutDecompressedLen < 1)
        {
            Log.Error("[Initial]A bad compressed gump packet was received. Unable to process.");
            return;
        }

        byte[]
            layoutBuffer =
                new byte[layoutDecompressedLen]; //System.Buffers.ArrayPool<byte>.Shared.Rent(layoutDecompressedLen);
        string layout = null;

        try
        {
            ZLib.Decompress(p.Buffer.Slice(p.Position, (int)layoutCompressedLen),
                layoutBuffer.AsSpan(0, layoutDecompressedLen));
            layout = Encoding.UTF8.GetString(layoutBuffer.AsSpan(0, layoutDecompressedLen));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to decompress or decode gump layout: {ex.Message}");
            return;
        }
        // finally
        // {
        //     System.Buffers.ArrayPool<byte>.Shared.Return(layoutBuffer);
        // }

        p.Skip((int)layoutCompressedLen);

        uint linesNum = p.ReadUInt32BE();
        string[] lines = new string[linesNum];

        try
        {
            if (linesNum != 0)
            {
                uint linesCompressedLen = p.ReadUInt32BE() - 4;
                int linesDecompressedLen = (int)p.ReadUInt32BE();

                if (linesDecompressedLen < 1)
                {
                    Log.Error("A bad compressed gump packet was received. Unable to process.");
                    return;
                }

                byte[]
                    linesBuffer =
                        new byte[linesDecompressedLen]; //System.Buffers.ArrayPool<byte>.Shared.Rent(linesDecompressedLen);

                ZLib.Decompress(p.Buffer.Slice(p.Position, (int)linesCompressedLen),
                    linesBuffer.AsSpan(0, linesDecompressedLen));
                p.Skip((int)linesCompressedLen);

                var reader = new StackDataReader(linesBuffer.AsSpan(0, linesDecompressedLen));

                for (int i = 0; i < linesNum; ++i)
                {
                    int remaining = reader.Remaining;

                    if (remaining >= 2)
                    {
                        int length = reader.ReadUInt16BE();

                        if (length > 0)
                            lines[i] = reader.ReadUnicodeBE(length);
                        else
                            lines[i] = string.Empty;
                    }
                    else
                        lines[i] = string.Empty;
                }

                reader.Release();

                // finally
                // {
                //     System.Buffers.ArrayPool<byte>.Shared.Return(linesBuffer);
                // }
            }

            if (string.IsNullOrEmpty(layout))
            {
                Log.Error("Gump layout is null or empty. Unable to create gump.");
                return;
            }

            Helpers.GumpHelpers.CreateGump(world, sender, gumpID, (int)x, (int)y, layout, lines);
        }
        catch (Exception e)
        {
            HtmlCrashLogGen.Generate(e.ToString(),
                description:
                "TazUO almost crashed, it was prevented but this was put in place for debugging, please post this on our discord.");
        }
    }
}
