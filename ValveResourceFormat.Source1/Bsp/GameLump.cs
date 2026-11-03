using System.Buffers.Binary;
using System.IO;

namespace ValveResourceFormat.Source1.Bsp;

internal sealed class BspGameLump
{
    private const int ItemSize = 16;
    private const int NameSize = 128;
    private const ushort CompressedFlag = 1;

    private readonly record struct Item(ushort Flags, ushort Version, int FileOffset, int FileLength, int StoredLength);

    private readonly BspFile bsp;
    private readonly Dictionary<string, Item> items = [];

    internal BspGameLump(BspFile bsp)
    {
        this.bsp = bsp;

        var lump = bsp.GetLumpData(LumpType.GameLump).Span;

        if (lump.Length < 4)
        {
            return;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(lump);
        count = Math.Min(count, (lump.Length - 4) / ItemSize);

        if (count <= 0)
        {
            return;
        }

        var raw = new (int Id, Item Item)[count];

        for (var i = 0; i < count; i++)
        {
            var entry = lump.Slice(4 + (i * ItemSize), ItemSize);
            var id = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            raw[i] = (id, new Item(flags, version, offset, length, length));
        }

        var lumpInfo = bsp.GetLumpInfo(LumpType.GameLump);
        var lumpEnd = lumpInfo.Offset + lumpInfo.Length;

        for (var i = 0; i < count; i++)
        {
            var (id, item) = raw[i];

            if (id == 0)
            {
                break;
            }

            if ((item.Flags & CompressedFlag) != 0)
            {
                var end = lumpEnd;

                if (i + 1 < count && raw[i + 1].Item.FileOffset > item.FileOffset)
                {
                    end = raw[i + 1].Item.FileOffset;
                }

                item = item with { FileLength = end - item.FileOffset };
            }

            items[GetIdString(id)] = item;
        }
    }

    private bool IsInsideFile(Item item)
    {
        return item.FileOffset >= 0 && item.FileLength >= 0 && (long)item.FileOffset + item.FileLength <= bsp.FileLength;
    }

    private static string GetIdString(int id)
    {
        Span<char> chars = stackalloc char[4];
        var length = 0;

        for (var shift = 24; shift >= 0; shift -= 8)
        {
            var value = (id >> shift) & 0x7f;

            if (value != 0)
            {
                chars[length++] = (char)value;
            }
        }

        return new string(chars[..length]);
    }

    /// <summary>Reads a count followed by that many names of 128 bytes, each ending at its first zero byte.</summary>
    /// <param name="span">The data of a game lump item.</param>
    /// <param name="position">Where the count starts, moved past the names.</param>
    internal static string[] ReadNameTable(ReadOnlySpan<byte> span, ref int position)
    {
        if (position + sizeof(int) > span.Length)
        {
            throw new InvalidDataException("Name table is truncated.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(span[position..]);
        position += sizeof(int);

        if (count < 0 || (long)count * NameSize > span.Length - position)
        {
            throw new InvalidDataException("Name table is truncated.");
        }

        var names = new string[count];

        for (var i = 0; i < count; i++)
        {
            var name = span.Slice(position, NameSize);
            var length = name.IndexOf((byte)0);
            names[i] = Source1Paths.DecodeName(length < 0 ? name : name[..length]);
            position += NameSize;
        }

        return names;
    }

    public bool HasItem(string id) => items.ContainsKey(id);

    public ushort GetItemVersion(string id) => items[id].Version;

    public byte[]? GetItemData(string id)
    {
        if (!items.TryGetValue(id, out var item))
        {
            return null;
        }

        if (!IsInsideFile(item))
        {
            throw new InvalidDataException($"Game lump item '{id}' lies outside the file.");
        }

        var raw = bsp.GetFileData(item.FileOffset, item.FileLength);

        if (BspFile.IsLzmaCompressed(raw.Span))
        {
            return BspFile.DecompressLzma(raw.Span, item.StoredLength);
        }

        return raw.ToArray();
    }
}
