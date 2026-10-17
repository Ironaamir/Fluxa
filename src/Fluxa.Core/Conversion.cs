using ImageMagick;

namespace Fluxa.Core;

public enum ConversionMode { Images, Video, Audio, Pdf }
public enum FramePolicy { Preserve, FirstFrame }
public sealed record ImageFormat(string Name, string Extension, MagickFormat Codec, bool MultipleFrames)
{
    public override string ToString() => $"{Name}  (.{Extension})";
}
public sealed record ConversionOptions(ImageFormat Format, string OutputFolder, uint Quality = 90,
    bool StripMetadata = true, FramePolicy Frames = FramePolicy.Preserve);
public sealed record ConversionResult(string Source, string Output, long Bytes, int Frames);

public interface IConversionEngine
{
    ConversionMode Mode { get; }
    Task<ConversionResult> ConvertAsync(string source, ConversionOptions options, CancellationToken cancellationToken);
}

public sealed class ImageConversionEngine : IConversionEngine
{
    public const long MaxInputBytes = 512L * 1024 * 1024;
    private static readonly (string Name, string Extension, string Codec)[] Candidates =
    [
        ("PNG", "png", "Png"), ("JPEG", "jpg", "Jpeg"), ("WebP", "webp", "WebP"),
        ("AVIF", "avif", "Avif"), ("TIFF", "tiff", "Tiff"), ("BMP", "bmp", "Bmp"),
        ("GIF", "gif", "Gif"), ("Windows icon", "ico", "Ico"), ("JPEG XL", "jxl", "Jxl"),
        ("JPEG 2000", "jp2", "Jp2"), ("Targa", "tga", "Tga"), ("Photoshop", "psd", "Psd"),
        ("OpenEXR", "exr", "Exr"), ("Radiance HDR", "hdr", "Hdr"),
        ("Portable pixmap", "ppm", "Ppm"), ("Portable graymap", "pgm", "Pgm"),
        ("Portable bitmap", "pbm", "Pbm"), ("QOI", "qoi", "Qoi"),
        ("HEIC (import)", "heic", "Heic"), ("HEIF (import)", "heif", "Heic"),
        ("DDS", "dds", "Dds"), ("PCX", "pcx", "Pcx"), ("X bitmap", "xbm", "Xbm")
    ];
    private static readonly Lazy<(IReadOnlyList<ImageFormat> Outputs, IReadOnlyDictionary<string, MagickFormat> Inputs)> Catalog = new(CreateCatalog);
    private static readonly SemaphoreSlim CodecGate = new(1, 1);

    public ConversionMode Mode => ConversionMode.Images;
    public static IReadOnlyList<ImageFormat> OutputFormats => Catalog.Value.Outputs;
    public static IReadOnlyCollection<string> InputExtensions => Catalog.Value.Inputs.Keys.ToArray();
    public static bool IsSupportedInput(string path) => Catalog.Value.Inputs.ContainsKey(Path.GetExtension(path));

    private static (IReadOnlyList<ImageFormat>, IReadOnlyDictionary<string, MagickFormat>) CreateCatalog()
    {
        ResourceLimits.Memory = 256UL * 1024 * 1024;
        ResourceLimits.Disk = 1024UL * 1024 * 1024;
        ResourceLimits.Width = 32768;
        ResourceLimits.Height = 32768;
        ResourceLimits.ListLength = 256;
        ResourceLimits.Thread = 2;
        var formats = MagickNET.SupportedFormats.ToDictionary(f => f.Format);
        var inputs = new Dictionary<string, MagickFormat>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<ImageFormat>();
        foreach (var item in Candidates)
        {
            if (!Enum.TryParse<MagickFormat>(item.Codec, true, out var codec) || !formats.TryGetValue(codec, out var info)) continue;
            if (info.SupportsReading) inputs["." + item.Extension] = codec;
            if (info.SupportsWriting && item.Extension is not ("heic" or "heif"))
                outputs.Add(new(item.Name, item.Extension, codec, info.SupportsMultipleFrames));
        }
        if (inputs.TryGetValue(".jpg", out var jpeg)) inputs[".jpeg"] = inputs[".jpe"] = jpeg;
        if (inputs.TryGetValue(".tiff", out var tiff)) inputs[".tif"] = tiff;
        return (outputs.AsReadOnly(), inputs);
    }

    public async Task<ConversionResult> ConvertAsync(string source, ConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        source = Path.GetFullPath(source);
        if (!Catalog.Value.Inputs.TryGetValue(Path.GetExtension(source), out var codec))
            throw new NotSupportedException("This input is not a supported raster image.");
        if (!OutputFormats.Contains(options.Format)) throw new NotSupportedException("That output format is unavailable in this codec build.");
        if (options.Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), "Quality must be 1–100.");
        if (string.IsNullOrWhiteSpace(options.OutputFolder)) throw new ArgumentException("Choose an output folder.", nameof(options));
        if (new FileInfo(source).Length > MaxInputBytes) throw new InvalidDataException("The input exceeds the 512 MB safety limit.");
        await CodecGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Convert(source, codec, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { CodecGate.Release(); }
    }

    private static ConversionResult Convert(string source, MagickFormat codec, ConversionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(source);
        using var images = new MagickImageCollection();
        // Explicit raster decoder + stream prevents ImageMagick filename syntax and external format autodetection.
        var settings = new MagickReadSettings { Format = codec };
        if (options.Frames == FramePolicy.FirstFrame) { settings.FrameIndex = 0; settings.FrameCount = 1; }
        images.Read(input, settings);
        token.ThrowIfCancellationRequested();
        if (images.Count == 0) throw new InvalidDataException("No image frames were found.");
        if (images.Count > 1 && !options.Format.MultipleFrames)
            throw new InvalidDataException("This image has multiple frames/pages. Choose a sequence-capable format or select ‘First frame only’.");
        // GIF/WebP disposal frames need coalescing before conversion to full-page formats.
        if (images.Count > 1 && codec is MagickFormat.Gif or MagickFormat.WebP) images.Coalesce();
        foreach (var image in images)
        {
            image.AutoOrient();
            image.Quality = options.Quality;
            if (options.StripMetadata) image.Strip();
            if (options.Format.Codec == MagickFormat.Jpeg)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
            if (options.Format.Codec == MagickFormat.Ico && (image.Width > 256 || image.Height > 256))
                image.Resize(new MagickGeometry(256, 256) { Greater = true });
        }
        var folder = Path.GetFullPath(options.OutputFolder);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".fluxa-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (images.Count == 1) images[0].Write(output, options.Format.Codec);
                else images.Write(output, options.Format.Codec);
            }
            token.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(source);
            for (var suffix = 0; ; suffix++)
            {
                var filename = stem + (suffix == 0 ? "" : $" ({suffix})") + "." + options.Format.Extension;
                var destination = Path.Combine(folder, filename);
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    return new(source, destination, new FileInfo(destination).Length, images.Count);
                }
                catch (IOException) when (File.Exists(destination)) { token.ThrowIfCancellationRequested(); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
