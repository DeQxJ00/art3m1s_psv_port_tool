// SPDX-License-Identifier: MPL-2.0
// PSB parsing is adapted from Alphaly2K/art3m1s-core. The resize/rebuild code is original.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Art3m1s.PsvTool.Core;

public sealed partial class PsbProcessor
{
    public async Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default)
    {
        if (ratio is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ratio));
        if (ratio == 1)
        {
            await InspectAsync(path, cancellationToken);
            return;
        }

        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken);
        MutableDocument document = MutableDocument.Parse(data);
        Dictionary<ResourceKey, byte[]> replacements = [];
        ResizeModel(document, ratio, replacements, cancellationToken);
        if (replacements.Count == 0)
            throw new InvalidDataException("The PSB contains no supported RGBA8 or DXT5 E-mote textures.");

        byte[] rebuilt = document.RebuildResources(replacements);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.psb");
        try
        {
            await File.WriteAllBytesAsync(temporary, rebuilt, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ResizeModel(MutableDocument document, double ratio,
        Dictionary<ResourceKey, byte[]> replacements, CancellationToken cancellationToken)
    {
        ObjectNode root = document.Root as ObjectNode ?? throw new InvalidDataException("PSB root is not an object.");
        ObjectNode sources = root.GetObject("source") ?? throw new InvalidDataException(
            $"E-mote PSB has no source table (root keys: {string.Join(", ", root.Values.Keys)}).");
        foreach ((string sourceName, Node sourceNode) in sources.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceNode is not ObjectNode source || source.GetObject("texture") is not { } texture) continue;
            string format = texture.GetString("type") ?? "unknown";
            if (!format.Equals("DXT5", StringComparison.OrdinalIgnoreCase) &&
                !format.Equals("RGBA8", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"PSB texture {sourceName} uses unsupported format {format}.");
            ResourceNode resource = (texture.Get("pixel") ?? texture.Get("data") ?? texture.Get("resource")) as ResourceNode
                ?? throw new InvalidDataException($"PSB texture {sourceName} has no pixel resource.");
            NumberNode widthNode = texture.GetNumber("width")
                ?? throw new InvalidDataException($"PSB texture {sourceName} has no width.");
            NumberNode heightNode = texture.GetNumber("height")
                ?? throw new InvalidDataException($"PSB texture {sourceName} has no height.");
            int width = checked((int)widthNode.Value);
            int height = checked((int)heightNode.Value);
            int targetWidth = Math.Max(1, (int)(width * ratio));
            int targetHeight = Math.Max(1, (int)(height * ratio));
            ResourceKey key = new(resource.Index, resource.Extra);
            if (!replacements.ContainsKey(key))
            {
                byte[] compressed = document.GetResource(key);
                byte[] rgba = format.Equals("DXT5", StringComparison.OrdinalIgnoreCase)
                    ? DecodeDxt5(compressed, width, height)
                    : ValidateRgba8(compressed, width, height);
                using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rgba, width, height);
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Bicubic,
                    Compand = false
                }));
                byte[] resized = new byte[targetWidth * targetHeight * 4];
                image.CopyPixelDataTo(resized);
                replacements.Add(key, format.Equals("DXT5", StringComparison.OrdinalIgnoreCase)
                    ? EncodeDxt5(resized, targetWidth, targetHeight)
                    : resized);
            }

            widthNode.ScaleDimension(ratio);
            heightNode.ScaleDimension(ratio);
            texture.GetNumber("truncated_width")?.ScaleDimension(ratio);
            texture.GetNumber("truncated_height")?.ScaleDimension(ratio);
            if (source.GetObject("icon") is { } icons)
            {
                foreach (Node iconNode in icons.Values.Values)
                    if (iconNode is ObjectNode icon)
                        foreach (string field in new[] { "left", "top", "width", "height", "originX", "originY" })
                            icon.GetNumber(field)?.Scale(ratio);
            }
        }

        if (root.GetObject("screenSize") is { } screen)
        {
            screen.GetNumber("width")?.ScaleDimension(ratio);
            screen.GetNumber("height")?.ScaleDimension(ratio);
        }

        if (root.GetObject("object") is { } objects)
            ScaleMotionGeometry(objects, ratio);
    }

    private static void ScaleMotionGeometry(Node node, double ratio)
    {
        switch (node)
        {
            case ListNode list:
                foreach (Node child in list.Values) ScaleMotionGeometry(child, ratio);
                break;
            case ObjectNode value:
                if (value.GetString("src")?.Equals("blank", StringComparison.Ordinal) == true &&
                    value.Get("icon") is StringNode blankDomain)
                    ScaleBlankMeshDomain(blankDomain, ratio);

                foreach ((string key, Node child) in value.Values)
                {
                    switch (key)
                    {
                        case "coord" when child is ListNode coordinates:
                            ScaleNumberList(coordinates, ratio);
                            break;
                        case "ox" or "oy" or "groundCorrection" when child is NumberNode coordinate:
                            coordinate.Scale(ratio);
                            break;
                        case "bounds" when child is ObjectNode bounds:
                            foreach (string field in new[] { "left", "top", "right", "bottom" })
                                bounds.GetNumber(field)?.Scale(ratio);
                            break;
                        case "cp" when child is ObjectNode path:
                            if (path.Get("x") is ListNode pathX) ScaleNumberList(pathX, ratio);
                            if (path.Get("y") is ListNode pathY) ScaleNumberList(pathY, ratio);
                            break;
                        default:
                            ScaleMotionGeometry(child, ratio);
                            break;
                    }
                }
                break;
        }
    }

    private static void ScaleNumberList(ListNode list, double ratio)
    {
        foreach (Node value in list.Values)
            if (value is NumberNode number) number.Scale(ratio);
    }

    private static void ScaleBlankMeshDomain(StringNode domain, double ratio)
    {
        string[] fields = domain.Value.Split(':');
        if (fields.Length != 4) return;
        for (int index = 0; index < fields.Length; index++)
        {
            if (!double.TryParse(fields[index], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value))
                return;
            double scaled = value * ratio;
            if (index < 2) scaled = Math.Max(1, scaled);
            fields[index] = Math.Truncate(scaled).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        domain.Rewrite(string.Join(':', fields));
    }

    private readonly record struct ResourceKey(int Index, bool Extra);

    private abstract class Node;
    private sealed class ObjectNode(Dictionary<string, Node> values) : Node
    {
        public Dictionary<string, Node> Values { get; } = values;
        public Node? Get(string key) => Values.GetValueOrDefault(key);
        public ObjectNode? GetObject(string key) => Get(key) as ObjectNode;
        public NumberNode? GetNumber(string key) => Get(key) as NumberNode;
        public string? GetString(string key) => (Get(key) as StringNode)?.Value;
    }
    private sealed class ListNode(List<Node> values) : Node { public List<Node> Values { get; } = values; }
    private sealed class StringNode(byte[] data, int offset, int byteLength, string value) : Node
    {
        public string Value { get; private set; } = value;

        public void Rewrite(string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.Length > byteLength)
                throw new InvalidDataException("Scaled PSB string no longer fits its original string-table slot.");
            data.AsSpan(offset, byteLength).Clear();
            encoded.CopyTo(data.AsSpan(offset));
            Value = value;
        }
    }
    private sealed class ResourceNode(int index, bool extra) : Node
    {
        public int Index { get; } = index;
        public bool Extra { get; } = extra;
    }
    private sealed class ScalarNode : Node;

    private sealed class NumberNode(byte[] data, int offset, byte kind, double value) : Node
    {
        public double Value { get; private set; } = value;

        public void Scale(double ratio) => Write(Value * ratio, dimension: false);
        public void ScaleDimension(double ratio) => Write(Math.Max(1, Math.Truncate(Value * ratio)), dimension: true);

        private void Write(double value, bool dimension)
        {
            switch (kind)
            {
                case 0x04:
                case 0x1d:
                    if (value != 0) throw new InvalidDataException("A zero-width PSB number cannot become non-zero.");
                    break;
                case >= 0x05 and <= 0x0c:
                    int width = kind - 0x04;
                    long integer = dimension ? checked((long)value) : checked((long)Math.Truncate(value));
                    long minimum = width == 8 ? long.MinValue : -(1L << (width * 8 - 1));
                    long maximum = width == 8 ? long.MaxValue : (1L << (width * 8 - 1)) - 1;
                    if (integer < minimum || integer > maximum) throw new InvalidDataException("Scaled PSB integer exceeds its encoded width.");
                    ulong bits = unchecked((ulong)integer);
                    for (int index = 0; index < width; index++) data[offset + 1 + index] = (byte)(bits >> (index * 8));
                    value = integer;
                    break;
                case 0x1e:
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 1), checked((float)value));
                    break;
                case 0x1f:
                    BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(offset + 1), value);
                    break;
                default: throw new InvalidDataException($"Unsupported PSB numeric type 0x{kind:X2}.");
            }
            Value = value;
        }
    }

    private sealed class CompactArray(uint[] values, int itemOffset, int itemWidth)
    {
        public uint[] Values { get; } = values;
        public int ItemOffset { get; } = itemOffset;
        public int ItemWidth { get; } = itemWidth;
    }

    private sealed class MutableDocument
    {
        private readonly byte[] _data;
        private readonly byte[] _header;
        private readonly uint? _headerKey;
        private readonly ushort _version;
        private readonly int _headerLength;
        private readonly uint _chunkData;
        private readonly uint _extraData;
        private readonly CompactArray _chunkOffsets;
        private readonly CompactArray _chunkLengths;
        private readonly CompactArray? _extraOffsets;
        private readonly CompactArray? _extraLengths;
        private readonly uint[] _sectionOffsets;

        private MutableDocument(byte[] data, byte[] header, uint? headerKey, ushort version, int headerLength,
            uint chunkData, uint extraData, CompactArray chunkOffsets, CompactArray chunkLengths,
            CompactArray? extraOffsets, CompactArray? extraLengths, uint[] sectionOffsets, Node root)
        {
            _data = data; _header = header; _headerKey = headerKey; _version = version; _headerLength = headerLength;
            _chunkData = chunkData; _extraData = extraData; _chunkOffsets = chunkOffsets; _chunkLengths = chunkLengths;
            _extraOffsets = extraOffsets; _extraLengths = extraLengths; _sectionOffsets = sectionOffsets; Root = root;
        }

        public Node Root { get; }

        public static MutableDocument Parse(byte[] data)
        {
            if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("PSB\0"u8)) throw new InvalidDataException("Missing PSB signature.");
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
            int headerLength = version switch { 1 or 2 => 40, 3 => 44, 4 => 56, _ => throw new InvalidDataException($"Unsupported PSB version {version}.") };
            byte[] header = data.AsSpan(0, headerLength).ToArray();
            uint? key = null;
            if ((flags & 1) != 0)
            {
                key = InferHeaderKey(header, (uint)headerLength);
                ApplyCipher(header.AsSpan(8), key.Value);
            }
            uint namesOffset = ReadUInt32(header, 12);
            if (data[namesOffset] is < 0x0d or > 0x14) throw new NotSupportedException("Encrypted PSB bodies cannot be resized safely.");
            CompactArray charset = ParseArray(data, checked((int)namesOffset));
            CompactArray nameTree = ParseArray(data, checked(charset.ItemOffset + charset.Values.Length * charset.ItemWidth));
            CompactArray nameIndexes = ParseArray(data, checked(nameTree.ItemOffset + nameTree.Values.Length * nameTree.ItemWidth));
            string[] names = DecodeNames(charset.Values, nameTree.Values, nameIndexes.Values);
            uint stringsOffset = ReadUInt32(header, 16);
            CompactArray stringOffsets = ParseArray(data, checked((int)stringsOffset));
            uint stringsData = ReadUInt32(header, 20);
            EncodedString[] strings = stringOffsets.Values.Select(offset =>
            {
                int address = checked((int)(stringsData + offset));
                int end = FindCStringEnd(data, address);
                return new EncodedString(Encoding.UTF8.GetString(data, address, end - address), address, end - address);
            }).ToArray();
            uint chunkOffsetsAddress = ReadUInt32(header, 24), chunkLengthsAddress = ReadUInt32(header, 28);
            CompactArray chunkOffsets = ParseArray(data, checked((int)chunkOffsetsAddress));
            CompactArray chunkLengths = ParseArray(data, checked((int)chunkLengthsAddress));
            if (chunkOffsets.Values.Length != chunkLengths.Values.Length) throw new InvalidDataException("PSB resource tables do not match.");
            CompactArray? extraOffsets = null, extraLengths = null;
            uint extraData = 0;
            if (version >= 4)
            {
                uint eo = ReadUInt32(header, 44), el = ReadUInt32(header, 48); extraData = ReadUInt32(header, 52);
                if (eo != 0 || el != 0 || extraData != 0)
                {
                    extraOffsets = ParseArray(data, checked((int)eo)); extraLengths = ParseArray(data, checked((int)el));
                    if (extraOffsets.Values.Length != extraLengths.Values.Length) throw new InvalidDataException("PSB extra-resource tables do not match.");
                }
            }
            ValueParser parser = new(data, names, strings);
            Node root = parser.Parse(checked((int)ReadUInt32(header, 36)), 0);
            uint[] sections = version >= 4
                ? [ReadUInt32(header, 12), ReadUInt32(header, 16), ReadUInt32(header, 20), chunkOffsetsAddress, chunkLengthsAddress, ReadUInt32(header, 32), ReadUInt32(header, 36), ReadUInt32(header, 44), ReadUInt32(header, 48), extraData]
                : [ReadUInt32(header, 12), ReadUInt32(header, 16), ReadUInt32(header, 20), chunkOffsetsAddress, chunkLengthsAddress, ReadUInt32(header, 32), ReadUInt32(header, 36)];
            return new MutableDocument(data, header, key, version, headerLength, ReadUInt32(header, 32), extraData,
                chunkOffsets, chunkLengths, extraOffsets, extraLengths, sections.Where(value => value != 0).ToArray(), root);
        }

        public byte[] GetResource(ResourceKey key)
        {
            CompactArray offsets = key.Extra ? _extraOffsets ?? throw new InvalidDataException("Missing PSB extra resources.") : _chunkOffsets;
            CompactArray lengths = key.Extra ? _extraLengths! : _chunkLengths;
            uint baseOffset = key.Extra ? _extraData : _chunkData;
            if ((uint)key.Index >= offsets.Values.Length) throw new InvalidDataException($"PSB resource {key.Index} is out of range.");
            return _data.AsSpan(checked((int)(baseOffset + offsets.Values[key.Index])), checked((int)lengths.Values[key.Index])).ToArray();
        }

        public byte[] RebuildResources(Dictionary<ResourceKey, byte[]> replacements)
        {
            List<ResourceRegion> regions = [];
            regions.Add(BuildRegion(false, _chunkData, _chunkOffsets, _chunkLengths, replacements));
            if (_extraOffsets is not null && _extraLengths is not null)
                regions.Add(BuildRegion(true, _extraData, _extraOffsets, _extraLengths, replacements));
            regions = regions.Where(region => region.NewBytes is not null).OrderBy(region => region.Start).ToList();
            using MemoryStream output = new(_data.Length);
            int cursor = 0;
            foreach (ResourceRegion region in regions)
            {
                output.Write(_data.AsSpan(cursor, region.Start - cursor));
                output.Write(region.NewBytes!);
                cursor = region.End;
            }
            output.Write(_data.AsSpan(cursor));
            byte[] result = output.ToArray();
            int[] headerFields = _version >= 4 ? [12, 16, 20, 24, 28, 32, 36, 44, 48, 52] : [12, 16, 20, 24, 28, 32, 36];
            foreach (int field in headerFields)
            {
                uint oldValue = ReadUInt32(_header, field);
                if (oldValue != 0) BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(field), checked((uint)MapPosition(oldValue, regions)));
            }
            if (_version >= 3)
            {
                byte[] checksum = new byte[_version >= 4 ? 44 : 32];
                _header.AsSpan(8, 32).CopyTo(checksum);
                if (_version >= 4) _header.AsSpan(44, 12).CopyTo(checksum.AsSpan(32));
                BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(40), Adler32(checksum));
            }
            byte[] encodedHeader = _header.ToArray();
            if (_headerKey.HasValue) ApplyCipher(encodedHeader.AsSpan(8), _headerKey.Value);
            encodedHeader.CopyTo(result, 0);
            return result;
        }

        private ResourceRegion BuildRegion(bool extra, uint baseOffset, CompactArray offsets, CompactArray lengths,
            Dictionary<ResourceKey, byte[]> replacements)
        {
            int regionEnd = checked((int)_sectionOffsets.Where(value => value > baseOffset).DefaultIfEmpty((uint)_data.Length).Min());
            List<(int Index, int Start, int Length)> items = offsets.Values.Select((offset, index) =>
                (index, checked((int)(baseOffset + offset)), checked((int)lengths.Values[index]))).OrderBy(item => item.Item2).ToList();
            using MemoryStream bytes = new();
            int oldCursor = checked((int)baseOffset);
            uint[] newOffsets = new uint[offsets.Values.Length], newLengths = new uint[lengths.Values.Length];
            foreach ((int index, int start, int length) in items)
            {
                if (start < oldCursor || start + length > regionEnd) throw new InvalidDataException("Overlapping or out-of-range PSB resources.");
                bytes.Write(_data.AsSpan(oldCursor, start - oldCursor));
                byte[] value = replacements.GetValueOrDefault(new ResourceKey(index, extra)) ?? _data.AsSpan(start, length).ToArray();
                newOffsets[index] = checked((uint)bytes.Position);
                newLengths[index] = checked((uint)value.Length);
                bytes.Write(value);
                oldCursor = start + length;
            }
            bytes.Write(_data.AsSpan(oldCursor, regionEnd - oldCursor));
            for (int index = 0; index < newOffsets.Length; index++)
            {
                WriteCompact(_data, offsets.ItemOffset + index * offsets.ItemWidth, offsets.ItemWidth, newOffsets[index]);
                WriteCompact(_data, lengths.ItemOffset + index * lengths.ItemWidth, lengths.ItemWidth, newLengths[index]);
            }
            return new ResourceRegion(checked((int)baseOffset), regionEnd, bytes.ToArray());
        }

        private static int MapPosition(uint position, List<ResourceRegion> regions)
        {
            int mapped = checked((int)position);
            foreach (ResourceRegion region in regions)
            {
                if (position >= region.End) mapped += region.NewBytes!.Length - (region.End - region.Start);
                else if (position > region.Start) throw new InvalidDataException("A PSB section begins inside resource data.");
            }
            return mapped;
        }
    }

    private sealed record ResourceRegion(int Start, int End, byte[]? NewBytes);

    private readonly record struct EncodedString(string Value, int Offset, int ByteLength);

    private sealed class ValueParser(byte[] data, string[] names, EncodedString[] strings)
    {
        public Node Parse(int offset, int depth)
        {
            if (depth > 512) throw new InvalidDataException("PSB object nesting is too deep.");
            byte kind = data[offset];
            return kind switch
            {
                0x00 or 0x01 or 0x02 or 0x03 => new ScalarNode(),
                0x04 => new NumberNode(data, offset, kind, 0),
                >= 0x05 and <= 0x0c => new NumberNode(data, offset, kind, SignExtend(ReadCompact(data.AsSpan(offset + 1, kind - 0x04)), kind - 0x04)),
                >= 0x0d and <= 0x14 => new ScalarNode(),
                >= 0x15 and <= 0x18 => CreateStringNode(checked((int)ReadCompact(data.AsSpan(offset + 1, kind - 0x14)))),
                >= 0x19 and <= 0x1c => new ResourceNode(checked((int)ReadCompact(data.AsSpan(offset + 1, kind - 0x18))), false),
                0x1d => new NumberNode(data, offset, kind, 0),
                0x1e => new NumberNode(data, offset, kind, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 1))),
                0x1f => new NumberNode(data, offset, kind, BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset + 1))),
                0x20 => ParseList(offset + 1, depth + 1),
                0x21 => ParseObject(offset + 1, depth + 1),
                >= 0x22 and <= 0x25 => new ResourceNode(checked((int)ReadCompact(data.AsSpan(offset + 1, kind - 0x21))), true),
                _ => throw new InvalidDataException($"Unsupported PSB value type 0x{kind:X2}.")
            };
        }

        private StringNode CreateStringNode(int index)
        {
            EncodedString value = strings[index];
            return new StringNode(data, value.Offset, value.ByteLength, value.Value);
        }

        private Node ParseList(int offset, int depth)
        {
            CompactArray array = ParseArray(data, offset);
            int baseOffset = array.ItemOffset + array.Values.Length * array.ItemWidth;
            return new ListNode(array.Values.Select(relative => Parse(checked(baseOffset + (int)relative), depth)).ToList());
        }

        private Node ParseObject(int offset, int depth)
        {
            CompactArray nameIndexes = ParseArray(data, offset);
            int next = nameIndexes.ItemOffset + nameIndexes.Values.Length * nameIndexes.ItemWidth;
            CompactArray offsets = ParseArray(data, next);
            if (nameIndexes.Values.Length != offsets.Values.Length) throw new InvalidDataException("PSB object tables do not match.");
            int baseOffset = offsets.ItemOffset + offsets.Values.Length * offsets.ItemWidth;
            Dictionary<string, Node> values = [];
            for (int index = 0; index < offsets.Values.Length; index++)
                values.Add(names[checked((int)nameIndexes.Values[index])], Parse(checked(baseOffset + (int)offsets.Values[index]), depth));
            return new ObjectNode(values);
        }
    }

    private static CompactArray ParseArray(byte[] data, int offset)
    {
        byte kind = data[offset];
        if (kind is < 0x0d or > 0x14) throw new InvalidDataException($"Expected PSB array at {offset}.");
        int countWidth = kind - 0x0c;
        int count = checked((int)ReadCompact(data.AsSpan(offset + 1, countWidth)));
        if (count > MaximumResources) throw new InvalidDataException("PSB array exceeds the safety limit.");
        byte widthKind = data[offset + 1 + countWidth];
        if (widthKind is < 0x0c or > 0x14) throw new InvalidDataException("Invalid PSB array item width.");
        int itemWidth = widthKind - 0x0c;
        if (count != 0 && itemWidth == 0) throw new InvalidDataException("Non-empty PSB array has zero-width items.");
        int itemOffset = offset + 2 + countWidth;
        uint[] values = new uint[count];
        for (int index = 0; index < count; index++) values[index] = checked((uint)ReadCompact(data.AsSpan(itemOffset + index * itemWidth, itemWidth)));
        return new CompactArray(values, itemOffset, itemWidth);
    }

    private static string[] DecodeNames(uint[] charset, uint[] tree, uint[] indexes)
    {
        string[] result = new string[indexes.Length];
        for (int index = 0; index < indexes.Length; index++)
        {
            int cursor = checked((int)indexes[index]); List<byte> bytes = [];
            for (int remaining = tree.Length + 1; remaining > 0; remaining--)
            {
                uint parent = tree[cursor]; uint delta = charset[parent]; uint value = checked((uint)cursor - delta);
                bytes.Add(checked((byte)value)); cursor = checked((int)parent); if (cursor == 0) break;
            }
            bytes.Reverse(); result[index] = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes)).TrimEnd('\0');
        }
        return result;
    }

    private static string ReadCString(byte[] data, int offset)
    {
        int end = FindCStringEnd(data, offset);
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    private static int FindCStringEnd(byte[] data, int offset)
    {
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) throw new InvalidDataException("Unterminated PSB string.");
        return end;
    }

    private static long SignExtend(ulong value, int width) => width >= 8 ? unchecked((long)value) : (long)(value << (64 - width * 8)) >> (64 - width * 8);

    private static void WriteCompact(byte[] data, int offset, int width, uint value)
    {
        if (width < 4 && value >= 1u << (width * 8)) throw new InvalidDataException("Rebuilt PSB offset exceeds its encoded width.");
        for (int index = 0; index < width; index++) data[offset + index] = (byte)(value >> (index * 8));
    }

    private static byte[] DecodeDxt5(byte[] data, int width, int height)
    {
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        if (data.Length != checked(blocksX * blocksY * 16)) throw new InvalidDataException("DXT5 resource size does not match its texture dimensions.");
        byte[] output = new byte[checked(width * height * 4)];
        for (int by = 0; by < blocksY; by++) for (int bx = 0; bx < blocksX; bx++) DecodeDxt5Block(data.AsSpan((by * blocksX + bx) * 16, 16), output, width, height, bx * 4, by * 4);
        return output;
    }

    private static byte[] ValidateRgba8(byte[] data, int width, int height)
    {
        if (data.Length != checked(width * height * 4))
            throw new InvalidDataException("RGBA8 resource size does not match its texture dimensions.");
        return data;
    }

    private static void DecodeDxt5Block(ReadOnlySpan<byte> block, byte[] output, int width, int height, int ox, int oy)
    {
        Span<byte> alpha = stackalloc byte[8]; BuildAlphaPalette(block[0], block[1], alpha);
        ulong alphaBits = 0; for (int i = 0; i < 6; i++) alphaBits |= (ulong)block[2 + i] << (i * 8);
        Span<byte> colors = stackalloc byte[16]; BuildColorPalette(BinaryPrimitives.ReadUInt16LittleEndian(block[8..]), BinaryPrimitives.ReadUInt16LittleEndian(block[10..]), colors);
        uint colorBits = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        for (int pixel = 0; pixel < 16; pixel++)
        {
            int x = ox + pixel % 4, y = oy + pixel / 4; if (x >= width || y >= height) continue;
            int c = (int)(colorBits >> (pixel * 2) & 3), a = (int)(alphaBits >> (pixel * 3) & 7), dst = (y * width + x) * 4;
            output[dst] = colors[c * 4]; output[dst + 1] = colors[c * 4 + 1]; output[dst + 2] = colors[c * 4 + 2]; output[dst + 3] = alpha[a];
        }
    }

    private static byte[] EncodeDxt5(byte[] rgba, int width, int height)
    {
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4; byte[] result = new byte[blocksX * blocksY * 16];
        Span<byte> blockPixels = stackalloc byte[64];
        for (int by = 0; by < blocksY; by++) for (int bx = 0; bx < blocksX; bx++)
        {
            for (int py = 0; py < 4; py++) for (int px = 0; px < 4; px++)
            {
                int x = Math.Min(width - 1, bx * 4 + px), y = Math.Min(height - 1, by * 4 + py);
                rgba.AsSpan((y * width + x) * 4, 4).CopyTo(blockPixels.Slice((py * 4 + px) * 4, 4));
            }
            EncodeDxt5Block(blockPixels, result.AsSpan((by * blocksX + bx) * 16, 16));
        }
        return result;
    }

    private static void EncodeDxt5Block(ReadOnlySpan<byte> pixels, Span<byte> output)
    {
        byte minA = 255, maxA = 0; for (int i = 0; i < 16; i++) { byte a = pixels[i * 4 + 3]; minA = Math.Min(minA, a); maxA = Math.Max(maxA, a); }
        output[0] = maxA; output[1] = minA; Span<byte> alphas = stackalloc byte[8]; BuildAlphaPalette(maxA, minA, alphas);
        ulong alphaBits = 0; for (int i = 0; i < 16; i++) alphaBits |= (ulong)NearestAlpha(pixels[i * 4 + 3], alphas) << (i * 3);
        for (int i = 0; i < 6; i++) output[2 + i] = (byte)(alphaBits >> (i * 8));
        int minIndex = 0, maxIndex = 0, minL = int.MaxValue, maxL = int.MinValue;
        for (int i = 0; i < 16; i++) { int l = pixels[i * 4] * 3 + pixels[i * 4 + 1] * 6 + pixels[i * 4 + 2]; if (l < minL) { minL = l; minIndex = i; } if (l > maxL) { maxL = l; maxIndex = i; } }
        ushort c0 = To565(pixels.Slice(maxIndex * 4, 3)), c1 = To565(pixels.Slice(minIndex * 4, 3));
        if (c0 <= c1) { (c0, c1) = (c1, c0); if (c0 == c1 && c0 < ushort.MaxValue) c0++; }
        BinaryPrimitives.WriteUInt16LittleEndian(output[8..], c0); BinaryPrimitives.WriteUInt16LittleEndian(output[10..], c1);
        Span<byte> colors = stackalloc byte[16]; BuildColorPalette(c0, c1, colors); uint colorBits = 0;
        for (int i = 0; i < 16; i++) colorBits |= (uint)NearestColor(pixels.Slice(i * 4, 3), colors) << (i * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output[12..], colorBits);
    }

    private static void BuildAlphaPalette(byte a0, byte a1, Span<byte> values)
    {
        values[0] = a0; values[1] = a1;
        if (a0 > a1) for (int i = 1; i <= 6; i++) values[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else { for (int i = 1; i <= 4; i++) values[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5); values[6] = 0; values[7] = 255; }
    }

    private static void BuildColorPalette(ushort c0, ushort c1, Span<byte> values)
    {
        Write565(c0, values); Write565(c1, values[4..]);
        for (int channel = 0; channel < 3; channel++) { values[8 + channel] = (byte)((2 * values[channel] + values[4 + channel]) / 3); values[12 + channel] = (byte)((values[channel] + 2 * values[4 + channel]) / 3); }
        values[3] = values[7] = values[11] = values[15] = 255;
    }

    private static ushort To565(ReadOnlySpan<byte> color) => (ushort)(((color[0] >> 3) << 11) | ((color[1] >> 2) << 5) | (color[2] >> 3));
    private static void Write565(ushort value, Span<byte> color) { int r = value >> 11 & 31, g = value >> 5 & 63, b = value & 31; color[0] = (byte)((r << 3) | (r >> 2)); color[1] = (byte)((g << 2) | (g >> 4)); color[2] = (byte)((b << 3) | (b >> 2)); }
    private static int NearestAlpha(byte value, ReadOnlySpan<byte> palette) { int best = 0, error = int.MaxValue; for (int i = 0; i < 8; i++) { int e = Math.Abs(value - palette[i]); if (e < error) { error = e; best = i; } } return best; }
    private static int NearestColor(ReadOnlySpan<byte> value, ReadOnlySpan<byte> palette) { int best = 0, error = int.MaxValue; for (int i = 0; i < 4; i++) { int dr = value[0] - palette[i * 4], dg = value[1] - palette[i * 4 + 1], db = value[2] - palette[i * 4 + 2]; int e = dr * dr + dg * dg * 2 + db * db; if (e < error) { error = e; best = i; } } return best; }
}
