using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Fluxa.Core;

public enum VideoQuality { High, Balanced, Compact }

public sealed record VideoFormat(string Name, string Extension, string VideoCodec, string? AudioCodec, bool SupportsAudio = true)
{
    public override string ToString() => $"{Name}  (.{Extension})";
}

public sealed record VideoConversionOptions(
    VideoFormat Format,
    string OutputFolder,
    VideoQuality Quality = VideoQuality.Balanced,
    bool IncludeAudio = true,
    bool StripMetadata = true);

public sealed record VideoConversionResult(string Source, string Output, long Bytes);

public sealed class VideoConversionEngine
{
    public const long MaxInputBytes = 4L * 1024 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public string? FfmpegOverride { get; set; }

    public VideoConversionEngine(string? ffmpegPath = null) => FfmpegOverride = ffmpegPath;

    public static IReadOnlyList<VideoFormat> OutputFormats { get; } =
    [
        new("MP4 · H.264", "mp4", "libx264", "aac"),
        new("WebM · VP9", "webm", "libvpx-vp9", "libopus"),
        new("Matroska · H.264", "mkv", "libx264", "aac"),
        new("QuickTime · H.264", "mov", "libx264", "aac"),
        new("AVI · MPEG-4", "avi", "mpeg4", "libmp3lame"),
        new("Animated GIF", "gif", "gif", null, SupportsAudio: false)
    ];

    public static IReadOnlyCollection<string> InputExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".mts", ".3gp", ".ogv"
    };

    public static bool IsSupportedInput(string path) => InputExtensions.Contains(Path.GetExtension(path));

    public string? ResolveFfmpegPath()
    {
        if (!string.IsNullOrWhiteSpace(FfmpegOverride))
            return File.Exists(FfmpegOverride) ? FfmpegOverride : null;
        return FindFfmpeg();
    }

    public static string? FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var links = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(links)) return links;

            var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                try
                {
                    foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*", SearchOption.TopDirectoryOnly))
                    {
                        var found = Directory.EnumerateFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null) return found;
                    }
                }
                catch { }
            }
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"C:\ProgramData\chocolatey\bin",
            @"C:\ffmpeg\bin"
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var direct = Path.Combine(root, "ffmpeg.exe");
            if (File.Exists(direct)) return direct;
            var scoop = Path.Combine(root, "scoop", "shims", "ffmpeg.exe");
            if (File.Exists(scoop)) return scoop;
            var sub = Path.Combine(root, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(sub)) return sub;
        }

        return null;
    }

    public async Task<VideoConversionResult> ConvertAsync(string source, VideoConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        source = Path.GetFullPath(source);
        if (!IsSupportedInput(source))
            throw new NotSupportedException("This input is not a supported video file.");
        if (!OutputFormats.Contains(options.Format))
            throw new NotSupportedException("That video output format is unavailable.");
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new ArgumentException("Choose an output folder.", nameof(options));
        var info = new FileInfo(source);
        if (!info.Exists)
            throw new FileNotFoundException("The source video file was not found.", source);
        if (info.Length > MaxInputBytes)
            throw new InvalidDataException("The input exceeds the 4 GB safety limit.");

        var ffmpeg = ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("FFmpeg was not found. Install FFmpeg (for example via 'winget install Gyan.FFmpeg') or place ffmpeg.exe in the app folder.");

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunFfmpegAsync(ffmpeg, source, options, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async Task<VideoConversionResult> RunFfmpegAsync(string ffmpeg, string source, VideoConversionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folder = Path.GetFullPath(options.OutputFolder);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".fluxa-{Guid.NewGuid():N}.{options.Format.Extension}");
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            BuildArguments(psi.ArgumentList, source, temporary, options);

            using var process = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && stderr.Length < 16384) stderr.AppendLine(e.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("Could not start FFmpeg.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            });

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                var detail = ExtractErrorSummary(stderr.ToString());
                throw new InvalidDataException(string.IsNullOrWhiteSpace(detail)
                    ? $"FFmpeg failed with exit code {process.ExitCode}."
                    : detail);
            }

            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidDataException("Video conversion produced an empty output file.");

            var stem = Path.GetFileNameWithoutExtension(source);
            for (var suffix = 0; ; suffix++)
            {
                var filename = stem + (suffix == 0 ? "" : $" ({suffix})") + "." + options.Format.Extension;
                var destination = Path.Combine(folder, filename);
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    return new(source, destination, new FileInfo(destination).Length);
                }
                catch (IOException) when (File.Exists(destination)) { token.ThrowIfCancellationRequested(); }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    public static void BuildArguments(ICollection<string> args, string source, string output, VideoConversionOptions options)
    {
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        args.Add("-nostdin");
        args.Add("-y");
        args.Add("-i");
        args.Add(source);

        if (options.StripMetadata)
        {
            args.Add("-map_metadata");
            args.Add("-1");
        }

        var format = options.Format;
        if (format.Extension == "gif")
        {
            var filter = options.Quality switch
            {
                VideoQuality.High => "fps=15,scale=640:-1:flags=lanczos",
                VideoQuality.Compact => "fps=10,scale=360:-1:flags=lanczos",
                _ => "fps=12,scale=480:-1:flags=lanczos"
            };
            args.Add("-vf");
            args.Add(filter);
            args.Add("-an");
        }
        else
        {
            args.Add("-c:v");
            args.Add(format.VideoCodec);
            switch (format.VideoCodec)
            {
                case "libx264":
                    args.Add("-crf");
                    args.Add(options.Quality switch
                    {
                        VideoQuality.High => "18",
                        VideoQuality.Compact => "28",
                        _ => "23"
                    });
                    args.Add("-preset");
                    args.Add("medium");
                    args.Add("-pix_fmt");
                    args.Add("yuv420p");
                    if (format.Extension is "mp4" or "mov")
                    {
                        args.Add("-movflags");
                        args.Add("+faststart");
                    }
                    break;
                case "libvpx-vp9":
                    args.Add("-crf");
                    args.Add(options.Quality switch
                    {
                        VideoQuality.High => "24",
                        VideoQuality.Compact => "38",
                        _ => "31"
                    });
                    args.Add("-b:v");
                    args.Add("0");
                    args.Add("-pix_fmt");
                    args.Add("yuv420p");
                    break;
                case "mpeg4":
                    args.Add("-qscale:v");
                    args.Add(options.Quality switch
                    {
                        VideoQuality.High => "3",
                        VideoQuality.Compact => "9",
                        _ => "5"
                    });
                    break;
            }

            if (options.IncludeAudio && format.SupportsAudio && format.AudioCodec != null)
            {
                args.Add("-c:a");
                args.Add(format.AudioCodec);
                args.Add("-b:a");
                args.Add(options.Quality == VideoQuality.Compact ? "128k" : "192k");
            }
            else
            {
                args.Add("-an");
            }
        }

        args.Add(output);
    }

    private static string ExtractErrorSummary(string stderr)
    {
        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault() ?? "";
    }
}
