using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Art3m1s.PsvTool.Core;

public interface IFfmpegProcessor
{
    Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default);
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

    public async Task ResizeAsync(string path, double ratio, CancellationToken cancellationToken = default)
    {
        VideoProbe probe = await ProbeAsync(path, cancellationToken);
        string outputCodec = probe.Codec.Equals("wmv3", StringComparison.OrdinalIgnoreCase) ? "wmv2" : probe.Codec;
        string quality = outputCodec.Equals("theora", StringComparison.OrdinalIgnoreCase) ? "8" : "2";
        string extension = Path.GetExtension(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{extension}");
        string ratioText = ratio.ToString("0.########", CultureInfo.InvariantCulture);
        string filter = $"scale=max(2,trunc(iw*{ratioText}/2)*2):max(2,trunc(ih*{ratioText}/2)*2):flags=bicubic";
        try
        {
            List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:v:0", "-map", "0:a?",
                "-vf", filter, "-c:v", outputCodec, "-q:v", quality, "-c:a", "copy"];
            if (extension.Equals(".dat", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("-f"); arguments.Add(NormalizeMuxer(probe.Format));
            }
            arguments.Add(temporary);
            try
            {
                await RunAsync(_ffmpeg, arguments, cancellationToken);
            }
            catch (InvalidOperationException) when (!outputCodec.Equals("mpeg4", StringComparison.OrdinalIgnoreCase) && !outputCodec.Equals("theora", StringComparison.OrdinalIgnoreCase) && !outputCodec.Equals("wmv2", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                int codecIndex = arguments.IndexOf("-c:v") + 1;
                arguments[codecIndex] = "mpeg4";
                await RunAsync(_ffmpeg, arguments, cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        string json = await RunAsync(_ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name:format=format_name", "-of", "json", path],
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
            throw new InvalidDataException($"No video stream found in {path}.");
        string codec = streams[0].GetProperty("codec_name").GetString()
            ?? throw new InvalidDataException($"Video codec is missing in {path}.");
        string format = document.RootElement.TryGetProperty("format", out JsonElement formatElement) && formatElement.TryGetProperty("format_name", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty : string.Empty;
        return new VideoProbe(codec, format);
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
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error.Trim()}");
        return output;
    }

    private static string FindTool(string name)
    {
        string executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string? configured = Environment.GetEnvironmentVariable("ART3M1S_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string local = Path.Combine(AppContext.BaseDirectory, "tools", executable);
        if (File.Exists(local)) return local;
        string? embedded = ExtractEmbeddedTool(executable);
        if (embedded is not null) return embedded;
        return executable;
    }

    private static string? ExtractEmbeddedTool(string executable)
    {
        Assembly assembly = typeof(FfmpegProcessor).Assembly;
        string resource = "Art3m1s.Tools." + executable;
        if (!assembly.GetManifestResourceNames().Contains(resource, StringComparer.Ordinal)) return null;
        string version = assembly.GetName().Version?.ToString() ?? "dev";
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "art3m1s_psv_port_tool", "ffmpeg", version);
        Directory.CreateDirectory(cache);
        string output = Path.Combine(cache, executable);
        string hashResource = resource + ".sha256";
        string? expected = null;
        using (Stream? hashStream = assembly.GetManifestResourceStream(hashResource))
        using (StreamReader? reader = hashStream is null ? null : new StreamReader(hashStream))
            expected = reader?.ReadToEnd().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!File.Exists(output) || expected is not null && !HashMatches(output, expected))
        {
            string temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
            using Stream source = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing embedded FFmpeg resource.");
            using (FileStream target = File.Create(temporary)) source.CopyTo(target);
            if (expected is not null && !HashMatches(temporary, expected)) { File.Delete(temporary); throw new InvalidDataException("Embedded FFmpeg SHA-256 mismatch."); }
            File.Move(temporary, output, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return output;
    }

    private static bool HashMatches(string path, string expected)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record VideoProbe(string Codec, string Format);
}
