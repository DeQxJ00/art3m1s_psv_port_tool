using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Art3m1s.PsvTool.Core;

public interface IPngProcessor
{
    Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default);
}

public sealed class PngProcessor : IPngProcessor
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] source = await File.ReadAllBytesAsync(path, cancellationToken);
        PngDocument document = PngDocument.Parse(source);
        int width = checked((int)document.Width);
        int height = checked((int)document.Height);
        int targetWidth = Math.Max(1, (int)(width * ratio));
        int targetHeight = Math.Max(1, (int)(height * ratio));
        string temporary = path + $".{Guid.NewGuid():N}.resize";
        try
        {
            if (document.ColorType == 3)
                await ResizeIndexedAsync(source, document, targetWidth, targetHeight, ratio, temporary, cancellationToken);
            else
                ResizeTrueColor(source, document, targetWidth, targetHeight, ratio, temporary);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task ResizeIndexedAsync(
        byte[] source,
        PngDocument document,
        int width,
        int height,
        double ratio,
        string outputPath,
        CancellationToken cancellationToken)
    {
        PngChunk paletteChunk = document.Chunks.FirstOrDefault(static chunk => chunk.Type == "PLTE")
            ?? throw new InvalidDataException("Indexed PNG is missing PLTE.");
        if (paletteChunk.Data.Length is 0 || paletteChunk.Data.Length % 3 != 0 || paletteChunk.Data.Length > 768)
            throw new InvalidDataException("Invalid PNG palette.");
        byte[] transparency = document.Chunks.FirstOrDefault(static chunk => chunk.Type == "tRNS")?.Data ?? [];
        Rgba32[] palette = new Rgba32[paletteChunk.Data.Length / 3];
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = new Rgba32(
                paletteChunk.Data[i * 3],
                paletteChunk.Data[i * 3 + 1],
                paletteChunk.Data[i * 3 + 2],
                i < transparency.Length ? transparency[i] : (byte)255);
        }

        using Image<Rgba32> image = Image.Load<Rgba32>(source);
        image.Mutate(context => context.Resize(CreateResizeOptions(width, height)));
        byte[] scanlines = new byte[checked(height * (1 + ((width * document.BitDepth + 7) / 8)))];
        Dictionary<uint, byte> colorCache = new();
        image.ProcessPixelRows(accessor =>
        {
            int rowBytes = (width * document.BitDepth + 7) / 8;
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rowStart = y * (rowBytes + 1);
                scanlines[rowStart] = 0;
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte index = FindNearestPaletteIndex(row[x], palette, colorCache);
                    WritePackedIndex(scanlines.AsSpan(rowStart + 1, rowBytes), x, document.BitDepth, index);
                }
            }
        });

        byte[] compressed;
        using (MemoryStream compressedStream = new())
        {
            await using (ZLibStream zlib = new(compressedStream, CompressionLevel.Optimal, true))
                await zlib.WriteAsync(scanlines, cancellationToken);
            compressed = compressedStream.ToArray();
        }

        await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await output.WriteAsync(Signature, cancellationToken);
        byte[] ihdr = document.Ihdr.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)height));
        await WriteChunkAsync(output, "IHDR", ihdr, cancellationToken);

        bool wroteData = false;
        foreach (PngChunk chunk in document.Chunks.Skip(1))
        {
            if (chunk.Type is "IHDR" or "IEND")
                continue;
            if (chunk.Type == "IDAT")
            {
                if (!wroteData)
                {
                    await WriteChunkAsync(output, "IDAT", compressed, cancellationToken);
                    wroteData = true;
                }
                continue;
            }
            byte[] data = ScaleTextualChunk(chunk, ratio);
            await WriteChunkAsync(output, chunk.Type, data, cancellationToken);
        }
        if (!wroteData)
            await WriteChunkAsync(output, "IDAT", compressed, cancellationToken);
        await WriteChunkAsync(output, "IEND", [], cancellationToken);
    }

    private static void ResizeTrueColor(byte[] source, PngDocument document, int width, int height, double ratio, string outputPath)
    {
        PngColorType colorType = document.ColorType switch
        {
            0 => PngColorType.Grayscale,
            2 => PngColorType.Rgb,
            4 => PngColorType.GrayscaleWithAlpha,
            6 => PngColorType.RgbWithAlpha,
            _ => throw new InvalidDataException($"Unsupported PNG color type {document.ColorType}.")
        };
        PngBitDepth bitDepth = document.BitDepth switch
        {
            1 => PngBitDepth.Bit1,
            2 => PngBitDepth.Bit2,
            4 => PngBitDepth.Bit4,
            8 => PngBitDepth.Bit8,
            16 => PngBitDepth.Bit16,
            _ => throw new InvalidDataException($"Unsupported PNG bit depth {document.BitDepth}.")
        };

        PngEncoder encoder = new()
        {
            ColorType = colorType,
            BitDepth = bitDepth,
            CompressionLevel = PngCompressionLevel.DefaultCompression,
            FilterMethod = PngFilterMethod.Adaptive
        };
        using MemoryStream resized = new();
        if (document.BitDepth == 16)
        {
            if (document.ColorType == 0) ResizeAndSave<L16>(source, width, height, resized, encoder);
            else if (document.ColorType == 2) ResizeAndSave<Rgb48>(source, width, height, resized, encoder);
            else if (document.ColorType == 4) ResizeAndSave<La32>(source, width, height, resized, encoder);
            else ResizeAndSave<Rgba64>(source, width, height, resized, encoder);
        }
        else
        {
            if (document.ColorType == 0) ResizeAndSave<L8>(source, width, height, resized, encoder);
            else if (document.ColorType == 2) ResizeAndSave<Rgb24>(source, width, height, resized, encoder);
            else if (document.ColorType == 4) ResizeAndSave<La16>(source, width, height, resized, encoder);
            else ResizeAndSave<Rgba32>(source, width, height, resized, encoder);
        }

        PngDocument encoded = PngDocument.Parse(resized.ToArray());
        using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(Signature);
        WriteChunk(output, "IHDR", encoded.Ihdr);
        byte[] imageData = encoded.Chunks.Where(static chunk => chunk.Type == "IDAT")
            .SelectMany(static chunk => chunk.Data).ToArray();
        bool wroteData = false;
        foreach (PngChunk chunk in document.Chunks.Skip(1))
        {
            if (chunk.Type is "IHDR" or "IEND")
                continue;
            if (chunk.Type == "IDAT")
            {
                if (!wroteData)
                {
                    WriteChunk(output, "IDAT", imageData);
                    wroteData = true;
                }
                continue;
            }
            WriteChunk(output, chunk.Type, ScaleTextualChunk(chunk, ratio));
        }
        if (!wroteData)
            WriteChunk(output, "IDAT", imageData);
        WriteChunk(output, "IEND", []);
    }

    private static void ResizeAndSave<TPixel>(byte[] source, int width, int height, Stream output, PngEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> image = Image.Load<TPixel>(source);
        image.Mutate(context => context.Resize(CreateResizeOptions(width, height)));
        image.Save(output, encoder);
    }

    private static ResizeOptions CreateResizeOptions(int width, int height) => new()
    {
        Size = new Size(width, height),
        Mode = ResizeMode.Stretch,
        Sampler = KnownResamplers.Bicubic,
        Compand = false,
        PremultiplyAlpha = true
    };

    private static byte FindNearestPaletteIndex(Rgba32 color, Rgba32[] palette, Dictionary<uint, byte> cache)
    {
        uint key = (uint)color.R | ((uint)color.G << 8) | ((uint)color.B << 16) | ((uint)color.A << 24);
        if (cache.TryGetValue(key, out byte cached))
            return cached;

        int bestDistance = int.MaxValue;
        byte best = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            Rgba32 candidate = palette[i];
            int da = color.A - candidate.A;
            int dr = color.R * color.A / 255 - candidate.R * candidate.A / 255;
            int dg = color.G * color.A / 255 - candidate.G * candidate.A / 255;
            int db = color.B * color.A / 255 - candidate.B * candidate.A / 255;
            int distance = dr * dr + dg * dg + db * db + da * da * 2;
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = checked((byte)i);
            if (distance == 0)
                break;
        }
        cache[key] = best;
        return best;
    }

    private static void WritePackedIndex(Span<byte> row, int x, int bitDepth, byte index)
    {
        if (bitDepth == 8)
        {
            row[x] = index;
            return;
        }
        int bitOffset = x * bitDepth;
        int shift = 8 - bitDepth - bitOffset % 8;
        int mask = (1 << bitDepth) - 1;
        row[bitOffset / 8] |= checked((byte)((index & mask) << shift));
    }

    private static byte[] ScaleTextualChunk(PngChunk chunk, double ratio)
    {
        return chunk.Type switch
        {
            "tEXt" => ScaleTextChunk(chunk.Data, ratio),
            "zTXt" => ScaleCompressedTextChunk(chunk.Data, ratio),
            "iTXt" => ScaleInternationalTextChunk(chunk.Data, ratio),
            _ => chunk.Data
        };
    }

    private static byte[] ScaleTextChunk(byte[] data, double ratio)
    {
        int separator = Array.IndexOf(data, (byte)0);
        if (separator < 1 || separator == data.Length - 1)
            return data;
        string value = Encoding.Latin1.GetString(data, separator + 1, data.Length - separator - 1);
        string scaled = ScaleCoordinateList(value, ratio);
        if (scaled == value)
            return data;
        byte[] encoded = Encoding.Latin1.GetBytes(scaled);
        byte[] result = new byte[separator + 1 + encoded.Length];
        data.AsSpan(0, separator + 1).CopyTo(result);
        encoded.CopyTo(result, separator + 1);
        return result;
    }

    private static byte[] ScaleCompressedTextChunk(byte[] data, double ratio)
    {
        int separator = Array.IndexOf(data, (byte)0);
        if (separator < 1 || separator + 2 > data.Length || data[separator + 1] != 0)
            return data;
        byte[] decoded;
        try { decoded = Decompress(data.AsSpan(separator + 2)); }
        catch (InvalidDataException) { return data; }
        string value = Encoding.Latin1.GetString(decoded);
        string scaled = ScaleCoordinateList(value, ratio);
        if (scaled == value)
            return data;
        byte[] compressed = Compress(Encoding.Latin1.GetBytes(scaled));
        byte[] result = new byte[separator + 2 + compressed.Length];
        data.AsSpan(0, separator + 2).CopyTo(result);
        compressed.CopyTo(result, separator + 2);
        return result;
    }

    private static byte[] ScaleInternationalTextChunk(byte[] data, double ratio)
    {
        int keywordEnd = Array.IndexOf(data, (byte)0);
        if (keywordEnd < 1 || keywordEnd + 3 > data.Length)
            return data;
        byte compressionFlag = data[keywordEnd + 1];
        if (compressionFlag > 1 || data[keywordEnd + 2] != 0)
            return data;
        int languageEnd = Array.IndexOf(data, (byte)0, keywordEnd + 3);
        if (languageEnd < 0)
            return data;
        int translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
        if (translatedEnd < 0)
            return data;
        int textStart = translatedEnd + 1;
        byte[] encodedText = data.AsSpan(textStart).ToArray();
        byte[] decoded;
        try { decoded = compressionFlag == 1 ? Decompress(encodedText) : encodedText; }
        catch (InvalidDataException) { return data; }
        string value = Encoding.UTF8.GetString(decoded);
        string scaled = ScaleCoordinateList(value, ratio);
        if (scaled == value)
            return data;
        byte[] replacement = Encoding.UTF8.GetBytes(scaled);
        if (compressionFlag == 1)
            replacement = Compress(replacement);
        byte[] result = new byte[textStart + replacement.Length];
        data.AsSpan(0, textStart).CopyTo(result);
        replacement.CopyTo(result, textStart);
        return result;
    }

    private static byte[] Decompress(ReadOnlySpan<byte> source)
    {
        using MemoryStream input = new(source.ToArray());
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Compress(ReadOnlySpan<byte> source)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, true))
            zlib.Write(source);
        return output.ToArray();
    }

    private static string ScaleCoordinateList(string value, double ratio)
    {
        if (!value.Contains(','))
            return value;
        string[] fields = value.Split(',');
        bool changed = false;
        for (int i = 0; i < fields.Length; i++)
        {
            string trimmed = fields[i].Trim();
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                continue;
            string replacement = ((int)(number * ratio)).ToString(CultureInfo.InvariantCulture);
            fields[i] = fields[i].Replace(trimmed, replacement, StringComparison.Ordinal);
            changed = true;
        }
        return changed ? string.Join(',', fields) : value;
    }

    private static async Task WriteChunkAsync(Stream output, string type, byte[] data, CancellationToken cancellationToken)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        await output.WriteAsync(length, cancellationToken);
        await output.WriteAsync(typeBytes, cancellationToken);
        await output.WriteAsync(data, cancellationToken);
        uint crc = Crc32.Compute(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        await output.WriteAsync(length, cancellationToken);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)data.Length));
        output.Write(value);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(value, Crc32.Compute(typeBytes, data));
        output.Write(value);
    }

    private sealed record PngChunk(string Type, byte[] Data);

    private sealed record PngDocument(uint Width, uint Height, int BitDepth, int ColorType, byte[] Ihdr, IReadOnlyList<PngChunk> Chunks)
    {
        public static PngDocument Parse(byte[] bytes)
        {
            if (bytes.Length < Signature.Length || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
                throw new InvalidDataException("Not a PNG file.");
            int cursor = Signature.Length;
            List<PngChunk> chunks = [];
            while (cursor <= bytes.Length - 12)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4));
                cursor += 4;
                if (length > int.MaxValue || cursor > bytes.Length - checked((int)length + 8))
                    throw new InvalidDataException("Truncated PNG chunk.");
                string type = Encoding.ASCII.GetString(bytes, cursor, 4);
                cursor += 4;
                byte[] data = bytes.AsSpan(cursor, checked((int)length)).ToArray();
                cursor += checked((int)length) + 4; // data + CRC
                chunks.Add(new PngChunk(type, data));
                if (type == "IEND") break;
            }
            PngChunk ihdr = chunks.FirstOrDefault(static chunk => chunk.Type == "IHDR")
                ?? throw new InvalidDataException("PNG is missing IHDR.");
            if (ihdr.Data.Length != 13)
                throw new InvalidDataException("Invalid PNG IHDR.");
            return new PngDocument(
                BinaryPrimitives.ReadUInt32BigEndian(ihdr.Data.AsSpan(0, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(ihdr.Data.AsSpan(4, 4)),
                ihdr.Data[8], ihdr.Data[9], ihdr.Data, chunks);
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in first) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            foreach (byte value in second) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }
}
