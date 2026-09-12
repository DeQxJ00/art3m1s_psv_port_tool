using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Art3m1s.PsvTool.Core;

public interface IPfsCodec
{
    Task<ExtractedArchive> ExtractAsync(
        string archivePath,
        string outputDirectory,
        PfsNameEncoding nameEncoding = PfsNameEncoding.Auto,
        CancellationToken cancellationToken = default);

    Task PackPf8Async(
        ExtractedArchive archive,
        string outputPath,
        CancellationToken cancellationToken = default);
}

public sealed class PfsCodec : IPfsCodec
{
    private const int HeaderSize = 11;

    public async Task<ExtractedArchive> ExtractAsync(
        string archivePath,
        string outputDirectory,
        PfsNameEncoding nameEncoding = PfsNameEncoding.Auto,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await using FileStream input = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        byte[] header = new byte[HeaderSize];
        await ReadExactlyAsync(input, header, cancellationToken);
        if (header[0] != 'p' || header[1] != 'f' || header[2] is not ((byte)'2' or (byte)'6' or (byte)'8'))
            throw new InvalidDataException($"Unsupported PFS header in {archivePath}.");

        char version = (char)header[2];
        uint indexSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(3, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(7, 4));
        if (indexSize < 4 || 7L + indexSize > input.Length || count > 10_000_000)
            throw new InvalidDataException("Invalid PFS index bounds.");

        byte[] index = GC.AllocateUninitializedArray<byte>(checked((int)indexSize));
        header.AsSpan(7, 4).CopyTo(index);
        await ReadExactlyAsync(input, index.AsMemory(4), cancellationToken);
        byte[]? xorKey = version == '8' ? SHA1.HashData(index) : null;

        int cursor = 4;
        List<(byte[] RawName, string Name, uint Offset, uint Size)> parsed = new(checked((int)count));
        for (uint i = 0; i < count; i++)
        {
            EnsureAvailable(index, cursor, 4);
            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor, 4));
            cursor += 4;
            if (nameLength == 0 || nameLength > 1024 * 1024)
                throw new InvalidDataException("Invalid PFS entry name length.");
            EnsureAvailable(index, cursor, checked((int)nameLength + 12));
            byte[] rawName = index.AsSpan(cursor, checked((int)nameLength)).ToArray();
            cursor += checked((int)nameLength);
            cursor += 4; // reserved
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor + 4, 4));
            cursor += 8;
            if ((ulong)offset + size > (ulong)input.Length)
                throw new InvalidDataException("PFS entry points outside the archive.");
            parsed.Add((rawName, DecodeName(rawName, nameEncoding), offset, size));
        }

        List<PfsEntry> entries = new(parsed.Count);
        foreach (var entry in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string safePath = GetSafeExtractionPath(outputDirectory, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            input.Position = entry.Offset;
            await using FileStream output = new(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await CopyEntryAsync(input, output, entry.Size, xorKey, cancellationToken);
            entries.Add(new PfsEntry(entry.RawName, NormalizeArchivePath(entry.Name), entry.Offset, entry.Size, safePath));
        }

        return new ExtractedArchive(version, entries);
    }

    public async Task PackPf8Async(ExtractedArchive archive, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        List<EntryForPack> files = [];
        foreach (PfsEntry entry in archive.Entries)
        {
            FileInfo file = new(entry.ExtractedPath);
            if (!file.Exists)
                throw new FileNotFoundException("Extracted PFS entry is missing.", file.FullName);
            if (file.Length > uint.MaxValue)
                throw new InvalidDataException($"PFS entry exceeds 4 GiB: {entry.Path}");
            files.Add(new EntryForPack(entry.RawName, entry.Path, entry.ExtractedPath, checked((uint)file.Length)));
        }

        int entryBytes = files.Sum(static file => checked(16 + file.RawName.Length));
        uint indexSize = checked((uint)(4 + entryBytes + 4 + files.Count * 8 + 8 + 4));
        uint dataOffset = checked(indexSize + 7);

        using MemoryStream indexStream = new(checked((int)indexSize));
        using BinaryWriter indexWriter = new(indexStream, Encoding.UTF8, true);
        indexWriter.Write(checked((uint)files.Count));
        uint runningOffset = dataOffset;
        List<uint> offsetRecordPositions = new(files.Count);
        foreach (EntryForPack file in files)
        {
            indexWriter.Write(checked((uint)file.RawName.Length));
            indexWriter.Write(file.RawName);
            offsetRecordPositions.Add(checked((uint)indexStream.Position));
            indexWriter.Write(0u);
            indexWriter.Write(runningOffset);
            indexWriter.Write(file.Size);
            runningOffset = checked(runningOffset + file.Size);
        }

        uint tablePosition = checked((uint)indexStream.Position);
        indexWriter.Write(checked((uint)files.Count + 1));
        foreach (uint position in offsetRecordPositions)
        {
            indexWriter.Write(position);
            indexWriter.Write(0u);
        }
        indexWriter.Write(0u);
        indexWriter.Write(0u);
        indexWriter.Write(tablePosition);
        indexWriter.Flush();
        if (indexStream.Length != indexSize)
            throw new InvalidOperationException("Internal PFS index size mismatch.");

        byte[] indexBytes = indexStream.ToArray();
        byte[] xorKey = SHA1.HashData(indexBytes);
        await using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        byte[] outputHeader = new byte[7];
        outputHeader[0] = (byte)'p';
        outputHeader[1] = (byte)'f';
        outputHeader[2] = (byte)'8';
        BinaryPrimitives.WriteUInt32LittleEndian(outputHeader.AsSpan(3), indexSize);
        await output.WriteAsync(outputHeader, cancellationToken);
        await output.WriteAsync(indexBytes, cancellationToken);

        foreach (EntryForPack file in files)
        {
            await using FileStream source = new(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await CopyEncryptedAsync(source, output, xorKey, cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
    }

    private static async Task CopyEntryAsync(Stream input, Stream output, uint size, byte[]? xorKey, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            uint remaining = size;
            int keyOffset = 0;
            while (remaining > 0)
            {
                int requested = (int)Math.Min((uint)buffer.Length, remaining);
                int read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException();
                if (xorKey is not null)
                {
                    for (int i = 0; i < read; i++)
                        buffer[i] ^= xorKey[(keyOffset + i) % xorKey.Length];
                    keyOffset = (keyOffset + read) % xorKey.Length;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= checked((uint)read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyEncryptedAsync(Stream input, Stream output, byte[] key, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int keyOffset = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                for (int i = 0; i < read; i++)
                    buffer[i] ^= key[(keyOffset + i) % key.Length];
                keyOffset = (keyOffset + read) % key.Length;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string DecodeName(byte[] rawName, PfsNameEncoding nameEncoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding strictUtf8 = new UTF8Encoding(false, true);
        Encoding shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return nameEncoding switch
        {
            PfsNameEncoding.Utf8 => strictUtf8.GetString(rawName),
            PfsNameEncoding.ShiftJis => shiftJis.GetString(rawName),
            _ => TryDecode(strictUtf8, rawName) ?? shiftJis.GetString(rawName)
        };
    }

    private static string? TryDecode(Encoding encoding, byte[] bytes)
    {
        try { return encoding.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string GetSafeExtractionPath(string root, string archivePath)
    {
        string normalized = NormalizeArchivePath(archivePath);
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException($"Unsafe absolute archive path: {archivePath}");
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(static part => part is "." or ".."))
            throw new InvalidDataException($"Unsafe archive path: {archivePath}");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive path escapes extraction root: {archivePath}");
        return candidate;
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

    private static void EnsureAvailable(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
            throw new InvalidDataException("Truncated PFS index.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private sealed record EntryForPack(byte[] RawName, string Path, string SourcePath, uint Size);
}
