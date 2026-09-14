using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Art3m1s.PsvTool.Core;

public interface IFfmpegProcessor
{
    Task ResizeAsync(string path, double ratio, bool convertToH264Mp4 = false, CancellationToken cancellationToken = default);
}

public sealed class FfmpegProcessor : IFfmpegProcessor
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    public FfmpegProcessor(string? ffmpeg = null, string? ffprobe = null)
    {
        _ffmpeg = ffmpeg ?? FindTool("ffmpeg");
        _ffprobe = ffprobe ?? FindTool("ffprobe");
    }

    public async Task ResizeAsync(string path, double ratio, bool convertToH264Mp4 = false, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(path);
        bool isDat = extension.Equals(".dat", StringComparison.OrdinalIgnoreCase);
        VideoProbe probe;
        try
        {
            probe = await ProbeAsync(path, cancellationToken);
        }
        catch (Exception exception) when (isDat && exception is FfmpegProcessException or InvalidDataException or JsonException)
        {
            // Artemis uses .dat for both video containers and unrelated binary data
            // such as font caches. An unrecognized DAT is not a broken video: leave it
            // byte-for-byte unchanged and let archive repacking retain its original name.
            return;
        }
        if (convertToH264Mp4)
        {
            await ConvertToH264Mp4Async(path, probe, ratio, isDat, cancellationToken);
            return;
        }

        string outputCodec = probe.Codec.Equals("wmv3", StringComparison.OrdinalIgnoreCase) ? "wmv2" : probe.Codec;
        string quality = outputCodec.Equals("theora", StringComparison.OrdinalIgnoreCase) ? "8" : "2";
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{extension}");
        (int targetWidth, int targetHeight) = CalculateScaledDimensions(probe.Width, probe.Height, ratio);
        string filter = FormattableString.Invariant($"scale={targetWidth}:{targetHeight}:flags=bicubic");
        string frameDirectory = Path.Combine(Path.GetTempPath(), "art3m1s-video-" + Guid.NewGuid().ToString("N"));
        string framePattern = Path.Combine(frameDirectory, "frame_%08d.png");
        Directory.CreateDirectory(frameDirectory);
        try
        {
            // VisualNovelUpscaler decodes every frame with vsync disabled, processes the
            // image sequence, then rebuilds it at the detected frame rate. Scaling during
            // extraction avoids an unnecessary second PNG pass while retaining that timing.
            await RunAsync(_ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:v:0",
                 "-vf", filter, "-qscale:v", "1", "-qmin", "1", "-qmax", "1",
                 "-fps_mode", "passthrough", "-threads", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), framePattern],
                cancellationToken);
            if (!Directory.EnumerateFiles(frameDirectory, "*.png", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidDataException($"No video frames were decoded from {path}.");

            List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y",
                "-r", probe.FrameRate, "-i", framePattern, "-i", path,
                "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "copy",
                "-c:v", outputCodec, "-r", probe.FrameRate, "-q:v", quality,
                "-threads", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)];
            if (extension.Equals(".dat", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("-f"); arguments.Add(NormalizeMuxer(probe.Format));
            }
            arguments.Add(temporary);
            await RunAsync(_ffmpeg, arguments, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(frameDirectory)) Directory.Delete(frameDirectory, true);
        }
    }

    private async Task ConvertToH264Mp4Async(
        string path,
        VideoProbe probe,
        double ratio,
        bool useDatDimensions,
        CancellationToken cancellationToken)
    {
        string destination = Path.ChangeExtension(path, ".mp4");
        bool replacesSource = destination.Equals(path, StringComparison.OrdinalIgnoreCase);
        if (!replacesSource && File.Exists(destination))
            throw new IOException($"Cannot convert video because the destination already exists: {destination}");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.mp4");
        (int targetWidth, int targetHeight) = useDatDimensions
            ? (960, 544)
            : CalculateScaledDimensions(probe.Width, probe.Height, ratio);
        try
        {
            // Loose videos are normalized to a PSV-friendly H.264/AAC MP4. The verified
            // reference fixes DAT video at 960x544; other containers retain Ratio sizing.
            await RunAsync(_ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", path,
                 "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"scale={targetWidth}:{targetHeight}:flags=bicubic",
                 "-c:v", "libx264", "-profile:v", "main", "-level:v", "3.1", "-pix_fmt", "yuv420p",
                 "-crf", "23", "-c:a", "aac", "-b:a", "128k", "-ar", "48000",
                 "-movflags", "+faststart", temporary], cancellationToken);
            File.Move(temporary, destination, replacesSource);
            if (!replacesSource) File.Delete(path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        string json = await RunAsync(_ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,avg_frame_rate,r_frame_rate:format=format_name", "-of", "json", path],
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
            throw new InvalidDataException($"No video stream found in {path}.");
        string codec = streams[0].GetProperty("codec_name").GetString()
            ?? throw new InvalidDataException($"Video codec is missing in {path}.");
        int width = streams[0].GetProperty("width").GetInt32();
        int height = streams[0].GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Video dimensions are invalid in {path}.");
        string frameRate = ReadFrameRate(streams[0]);
        string format = document.RootElement.TryGetProperty("format", out JsonElement formatElement) && formatElement.TryGetProperty("format_name", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty : string.Empty;
        return new VideoProbe(codec, format, width, height, frameRate);
    }

    internal static (int Width, int Height) CalculateScaledDimensions(int width, int height, double ratio)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (ratio <= 0 || ratio > 1 || double.IsNaN(ratio) || double.IsInfinity(ratio))
            throw new ArgumentOutOfRangeException(nameof(ratio));

        // VisualNovelUpscaler resizes extracted frames with Python int(), which truncates
        // positive dimensions toward zero. Do not round or force dimensions to be even.
        return (Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)));
    }

    private static string ReadFrameRate(JsonElement stream)
    {
        foreach (string propertyName in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            if (!stream.TryGetProperty(propertyName, out JsonElement property)) continue;
            string? rational = property.GetString();
            if (string.IsNullOrWhiteSpace(rational)) continue;
            string[] parts = rational.Split('/');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) ||
                denominator == 0) continue;
            double value = numerator / denominator;
            if (value > 0 && double.IsFinite(value))
                return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        throw new InvalidDataException("The video frame rate is missing or invalid.");
    }

    private static string NormalizeMuxer(string format)
    {
        string primary = format.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "mpeg";
        return primary switch { "mov" => "mp4", "matroska" => "matroska", "mpegvideo" => "mpeg", _ => primary };
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {executable}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
            throw new FfmpegProcessException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error.Trim()}");
        return output;
    }

    private static string FindTool(string name)
    {
        string executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string? configured = Environment.GetEnvironmentVariable("ART3M1S_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string local = Path.Combine(AppContext.BaseDirectory, "tools", executable);
        if (File.Exists(local)) return local;
        local = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(local)) return local;
        return executable;
    }

    private sealed record VideoProbe(string Codec, string Format, int Width, int Height, string FrameRate);

    private sealed class FfmpegProcessException(string message) : InvalidOperationException(message);
}
