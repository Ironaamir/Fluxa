using Fluxa.Core;
using ImageMagick;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fluxa.Tests;

public sealed class ConversionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Fluxa.Tests", Guid.NewGuid().ToString("N"));
    private readonly ImageConversionEngine _engine = new();
    public ConversionTests() => Directory.CreateDirectory(_folder);
    private ImageFormat Format(string extension) => ImageConversionEngine.OutputFormats.Single(f => f.Extension == extension);
    private string Source(string name = "source.png", bool transparent = false)
    {
        var path = Path.Combine(_folder, name);
        using var image = new MagickImage(transparent ? MagickColors.Transparent : MagickColors.ForestGreen, 32, 24);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    public static IEnumerable<object[]> Outputs() => ImageConversionEngine.OutputFormats.Select(f => new object[] { f.Extension });

    [Theory]
    [MemberData(nameof(Outputs))]
    public async Task EveryAdvertisedOutputEncodesAndDecodes(string extension)
    {
        var source = Source();
        var result = await _engine.ConvertAsync(source, new(Format(extension), Path.Combine(_folder, "output")));
        Assert.True(result.Bytes > 0);
        using var decoded = new MagickImage(result.Output);
        Assert.Equal(32u, decoded.Width);
        Assert.Equal(24u, decoded.Height);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(result.Output)!, "*.tmp"));
        var roundTrip = await _engine.ConvertAsync(result.Output, new(Format("png"), Path.Combine(_folder, "roundtrip"), Frames: FramePolicy.FirstFrame));
        using var roundTripImage = new MagickImage(roundTrip.Output);
        Assert.Equal(32u, roundTripImage.Width);
        Assert.Equal(24u, roundTripImage.Height);
    }

    [Fact]
    public async Task OriginalsAndExistingFilesAreNeverOverwritten()
    {
        var source = Source("name.png");
        var bytes = File.ReadAllBytes(source);
        var first = await _engine.ConvertAsync(source, new(Format("png"), _folder));
        var second = await _engine.ConvertAsync(source, new(Format("png"), _folder));
        Assert.EndsWith("name (1).png", first.Output);
        Assert.EndsWith("name (2).png", second.Output);
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task TransparentJpegIsFlattenedToWhite()
    {
        var result = await _engine.ConvertAsync(Source(transparent: true), new(Format("jpg"), Path.Combine(_folder, "output")));
        using var image = new MagickImage(result.Output);
        Assert.False(image.HasAlpha);
        using var pixels = image.GetPixels();
        var color = pixels.GetPixel(0, 0).ToColor()!;
        Assert.True(color.R >= 250 && color.G >= 250 && color.B >= 250);
    }

    [Fact]
    public async Task PngRetainsTransparency()
    {
        var result = await _engine.ConvertAsync(Source(transparent: true), new(Format("png"), Path.Combine(_folder, "output")));
        using var image = new MagickImage(result.Output);
        Assert.True(image.HasAlpha);
        using var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).ToColor()!.A);
    }

    private string Animation()
    {
        var source = Path.Combine(_folder, "animation.gif");
        using var collection = new MagickImageCollection();
        collection.Add(new MagickImage(MagickColors.Red, 20, 20) { AnimationDelay = 12 });
        collection.Add(new MagickImage(MagickColors.Blue, 20, 20) { AnimationDelay = 20 });
        collection.Write(source, MagickFormat.Gif);
        return source;
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("tiff")]
    [InlineData("webp")]
    public async Task SequenceCapableOutputsRetainFrames(string extension)
    {
        var result = await _engine.ConvertAsync(Animation(), new(Format(extension), Path.Combine(_folder, "output")));
        using var images = new MagickImageCollection(result.Output);
        Assert.Equal(2, images.Count);
        Assert.Equal(2, result.Frames);
    }

    [Fact]
    public async Task MultiFrameToJpegRequiresExplicitFirstFramePolicy()
    {
        var source = Animation();
        await Assert.ThrowsAsync<InvalidDataException>(() => _engine.ConvertAsync(source, new(Format("jpg"), _folder)));
        var result = await _engine.ConvertAsync(source, new(Format("jpg"), _folder, Frames: FramePolicy.FirstFrame));
        Assert.Equal(1, result.Frames);
    }

    [Fact]
    public async Task IconIsResizedWithinWindowsLimit()
    {
        var path = Path.Combine(_folder, "large.png");
        using (var image = new MagickImage(MagickColors.Green, 512, 512)) image.Write(path);
        var result = await _engine.ConvertAsync(path, new(Format("ico"), _folder));
        using var icon = new MagickImage(result.Output);
        Assert.Equal(256u, icon.Width);
        Assert.Equal(256u, icon.Height);
    }

    [Fact]
    public async Task UnsafeAndFutureFormatInputsAreRejected()
    {
        foreach (var extension in new[] { ".pdf", ".svg", ".mp4", ".mp3", ".mvg", ".txt" })
            await Assert.ThrowsAsync<NotSupportedException>(() => _engine.ConvertAsync(Path.Combine(_folder, "file" + extension), new(Format("png"), _folder)));
    }

    [Fact]
    public async Task DecoderDoesNotFollowContentDisguisedAsRaster()
    {
        var source = Path.Combine(_folder, "fake.png");
        File.WriteAllText(source, "<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.com/x'/></svg>");
        await Assert.ThrowsAnyAsync<MagickException>(() => _engine.ConvertAsync(source, new(Format("jpg"), _folder)));
        Assert.Empty(Directory.GetFiles(_folder, "*.jpg"));
    }

    [Fact]
    public async Task CorruptInputDoesNotLeavePartialOutput()
    {
        var source = Path.Combine(_folder, "broken.png");
        File.WriteAllText(source, "not an image");
        await Assert.ThrowsAnyAsync<MagickException>(() => _engine.ConvertAsync(source, new(Format("png"), Path.Combine(_folder, "out"))));
        Assert.False(Directory.Exists(Path.Combine(_folder, "out")));
    }

    [Fact]
    public async Task PreCancelledConversionCreatesNoFiles()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var output = Path.Combine(_folder, "output");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ConvertAsync(Source(), new(Format("png"), output), cts.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task QualityValidationIsEnforced()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _engine.ConvertAsync(Source(), new(Format("jpg"), _folder, 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _engine.ConvertAsync(Source(), new(Format("jpg"), _folder, 101)));
    }

    [Fact]
    public async Task MetadataCanBeRemovedOrRetained()
    {
        var path = Path.Combine(_folder, "metadata.png");
        using (var image = new MagickImage(MagickColors.Green, 20, 20))
        {
            image.SetAttribute("comment", "private metadata");
            image.Write(path);
        }
        var retained = await _engine.ConvertAsync(path, new(Format("png"), _folder, StripMetadata: false));
        var removed = await _engine.ConvertAsync(path, new(Format("png"), _folder, StripMetadata: true));
        using var keep = new MagickImage(retained.Output);
        using var strip = new MagickImage(removed.Output);
        Assert.Equal("private metadata", keep.GetAttribute("comment"));
        Assert.Null(strip.GetAttribute("comment"));
    }

    [Fact]
    public async Task ConcurrentRequestsSerializeAndKeepUniqueOutputs()
    {
        var path = Source();
        var options = new ConversionOptions(Format("png"), Path.Combine(_folder, "output"));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _engine.ConvertAsync(path, options)));
        Assert.Equal(5, results.Select(r => r.Output).Distinct().Count());
        Assert.All(results, r => Assert.True(File.Exists(r.Output)));
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
