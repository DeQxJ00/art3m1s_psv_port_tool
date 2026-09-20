// SPDX-License-Identifier: MPL-2.0
// PSB header and resource-table parsing is adapted from Alphaly2K/art3m1s-core.
using System.Buffers.Binary;

namespace Art3m1s.PsvTool.Core;

public sealed record PsbInspection(
    ushort Version,
    ushort EncryptionFlags,
    int ResourceCount,
    int ExtraResourceCount,
    bool BodyEncrypted);

public interface IPsbProcessor
{
    Task<PsbInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates Artemis/E-mote PSB containers. The header and resource-table handling
/// follows art3m1s-core's independent PSB parser; resize and resource rebuilding are
/// implemented by the other partial declaration.
/// </summary>
public sealed partial class PsbProcessor : IPsbProcessor
{
    private const uint Key1 = 123_456_789;
    private const uint Key2 = 362_436_069;
    private const uint Key3 = 521_288_629;
    private const int MaximumResources = 4_000_000;

    public async Task<PsbInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        byte[] prefix = new byte[8];
        await ReadExactlyAtAsync(stream, prefix, 0, cancellationToken);
        if (!prefix.AsSpan(0, 4).SequenceEqual("PSB\0"u8))
            throw new InvalidDataException("Missing PSB signature.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(4));
        ushort encryptionFlags = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(6));
        int headerLength = version switch
        {
            1 or 2 => 40,
            3 => 44,
            4 => 56,
            _ => throw new InvalidDataException($"Unsupported PSB version {version}.")
        };
        if (stream.Length < headerLength)
            throw new InvalidDataException("Truncated PSB header.");

        byte[] header = new byte[headerLength];
        await ReadExactlyAtAsync(stream, header, 0, cancellationToken);
        if ((encryptionFlags & 1) != 0)
        {
            uint key = InferHeaderKey(header, checked((uint)headerLength));
            ApplyCipher(header.AsSpan(8), key);
        }

        uint encodedHeaderLength = ReadUInt32(header, 8);
        if (encodedHeaderLength is not 0 && encodedHeaderLength != headerLength)
            throw new InvalidDataException($"Unexpected PSB v{version} header length {encodedHeaderLength}.");

        uint names = ReadUInt32(header, 12);
        uint strings = ReadUInt32(header, 16);
        uint stringsData = ReadUInt32(header, 20);
        uint chunkOffsets = ReadUInt32(header, 24);
        uint chunkLengths = ReadUInt32(header, 28);
        uint chunkData = ReadUInt32(header, 32);
        uint entries = ReadUInt32(header, 36);
        ValidateOffsets(stream.Length, names, strings, stringsData, chunkOffsets, chunkLengths, chunkData, entries);

        if (version >= 3)
        {
            uint expected = ReadUInt32(header, 40);
            byte[] checksumBytes = new byte[version >= 4 ? 44 : 32];
            header.AsSpan(8, 32).CopyTo(checksumBytes);
            if (version >= 4) header.AsSpan(44, 12).CopyTo(checksumBytes.AsSpan(32));
            uint actual = Adler32(checksumBytes);
            if (expected != actual)
                throw new InvalidDataException($"PSB header checksum mismatch: expected {expected:X8}, got {actual:X8}.");
        }

        byte[] bodyMarker = new byte[1];
        await ReadExactlyAtAsync(stream, bodyMarker, names, cancellationToken);
        bool bodyEncrypted = bodyMarker[0] is < 0x0d or > 0x14;
        if (bodyEncrypted)
            return new PsbInspection(version, encryptionFlags, -1, -1, true);

        uint[] offsets = await ReadCompactArrayAsync(stream, chunkOffsets, cancellationToken);
        uint[] lengths = await ReadCompactArrayAsync(stream, chunkLengths, cancellationToken);
        if (offsets.Length != lengths.Length)
            throw new InvalidDataException("PSB resource offset and length tables do not match.");
        ValidateResourceRanges(stream.Length, chunkData, offsets, lengths);

        int extraCount = 0;
        if (version >= 4)
        {
            uint extraOffsetsAddress = ReadUInt32(header, 44);
            uint extraLengthsAddress = ReadUInt32(header, 48);
            uint extraDataAddress = ReadUInt32(header, 52);
            if (extraOffsetsAddress != 0 || extraLengthsAddress != 0 || extraDataAddress != 0)
            {
                ValidateOffsets(stream.Length, extraOffsetsAddress, extraLengthsAddress, extraDataAddress);
                uint[] extraOffsets = await ReadCompactArrayAsync(stream, extraOffsetsAddress, cancellationToken);
                uint[] extraLengths = await ReadCompactArrayAsync(stream, extraLengthsAddress, cancellationToken);
                if (extraOffsets.Length != extraLengths.Length)
                    throw new InvalidDataException("PSB extra-resource offset and length tables do not match.");
                ValidateResourceRanges(stream.Length, extraDataAddress, extraOffsets, extraLengths);
                extraCount = extraOffsets.Length;
            }
        }

        return new PsbInspection(version, encryptionFlags, offsets.Length, extraCount, false);
    }

    private static async Task<uint[]> ReadCompactArrayAsync(FileStream stream, uint offset, CancellationToken cancellationToken)
    {
        byte[] start = new byte[10];
        int available = checked((int)Math.Min(start.Length, stream.Length - offset));
        await ReadExactlyAtAsync(stream, start.AsMemory(0, available), offset, cancellationToken);
        byte kind = start[0];
        if (kind is < 0x0d or > 0x14)
            throw new InvalidDataException($"Expected PSB array at {offset}, found 0x{kind:X2}.");
        int countWidth = kind - 0x0c;
        ulong countValue = ReadCompact(start.AsSpan(1, countWidth));
        if (countValue > MaximumResources)
            throw new InvalidDataException($"PSB resource count {countValue} exceeds the safety limit.");
        int count = checked((int)countValue);
        byte widthKind = start[1 + countWidth];
        if (widthKind is < 0x0c or > 0x14)
            throw new InvalidDataException($"Invalid PSB array width marker 0x{widthKind:X2}.");
        int itemWidth = widthKind - 0x0c;
        if (count != 0 && itemWidth == 0)
            throw new InvalidDataException("Non-empty PSB array uses zero-width items.");
        int byteCount = checked(count * itemWidth);
        byte[] data = new byte[byteCount];
        await ReadExactlyAtAsync(stream, data, checked(offset + (uint)(2 + countWidth)), cancellationToken);
        uint[] result = new uint[count];
        for (int index = 0; index < count; index++)
        {
            ulong value = ReadCompact(data.AsSpan(index * itemWidth, itemWidth));
            result[index] = checked((uint)value);
        }
        return result;
    }

    private static ulong ReadCompact(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 8) throw new InvalidDataException("PSB compact integer exceeds 8 bytes.");
        ulong value = 0;
        for (int index = 0; index < bytes.Length; index++) value |= (ulong)bytes[index] << (index * 8);
        return value;
    }

    private static void ValidateOffsets(long fileLength, params uint[] offsets)
    {
        foreach (uint offset in offsets)
            if (offset >= fileLength)
                throw new InvalidDataException($"PSB offset {offset} exceeds file length {fileLength}.");
    }

    private static void ValidateResourceRanges(long fileLength, uint baseOffset, uint[] offsets, uint[] lengths)
    {
        for (int index = 0; index < offsets.Length; index++)
        {
            ulong end = (ulong)baseOffset + offsets[index] + lengths[index];
            if (end > (ulong)fileLength)
                throw new InvalidDataException($"PSB resource {index} exceeds the file bounds.");
        }
    }

    private static uint InferHeaderKey(ReadOnlySpan<byte> header, uint expectedLength)
    {
        uint encrypted = ReadUInt32(header, 8);
        uint firstStreamWord = encrypted ^ expectedLength;
        uint a = Key1 ^ (Key1 << 11);
        uint rhs = firstStreamWord ^ a ^ (a >> 8);
        return rhs ^ (rhs >> 19);
    }

    private static void ApplyCipher(Span<byte> bytes, uint key4)
    {
        uint key1 = Key1, key2 = Key2, key3 = Key3, current = 0;
        foreach (ref byte value in bytes)
        {
            if (current == 0)
            {
                uint a = key1 ^ (key1 << 11);
                uint next = a ^ key4 ^ ((a ^ (key4 >> 11)) >> 8);
                key1 = key2;
                key2 = key3;
                key3 = key4;
                key4 = next;
                current = next;
            }
            value ^= (byte)current;
            current >>= 8;
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static uint Adler32(ReadOnlySpan<byte> bytes)
    {
        const uint modulus = 65_521;
        uint a = 1, b = 0;
        foreach (byte value in bytes)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }

    private static async Task ReadExactlyAtAsync(FileStream stream, Memory<byte> buffer, long offset, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await RandomAccess.ReadAsync(stream.SafeFileHandle, buffer[total..], offset + total, cancellationToken);
            if (read == 0) throw new EndOfStreamException("Truncated PSB data.");
            total += read;
        }
    }
}
