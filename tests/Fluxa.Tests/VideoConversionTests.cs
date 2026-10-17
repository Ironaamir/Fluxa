using System.Diagnostics;
using Fluxa.App;
using Fluxa.Core;
using Xunit;

namespace Fluxa.Tests;

public sealed class VideoConversionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Fluxa.VideoTests", Guid.NewGuid().ToString("N"));
    private readonly VideoConversionEngine _engine = new();

    public VideoConversionTests() => Directory.CreateDirectory(_folder);

    private static VideoFormat Format(string extension) => VideoConversionEngine.OutputFormats.Single(f => f.Extension == extension);

    private string CreateSampleVideo(string name = "sample.mp4")
    {
        var ffmpeg = _engine.ResolveFfmpegPath();
        Assert.False(string.IsNullOrWhiteSpace(ffmpeg), "FFmpeg should be discoverable on this machine.");
        var path = Path.Combine(_folder, name);
        var psi = new ProcessStartInfo(ffmpeg!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("color=c=blue:s=64x48:d=0.4:r=10");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("sine=frequency=440:duration=0.4");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
        return path;
    }

    public static IEnumerable<object[]> Outputs() => VideoConversionEngine.OutputFormats.Select(f => new object[] { f.Extension });

    [Theory]
    [MemberData(nameof(Outputs))]
    public async Task EveryAdvertisedVideoFormatEncodes(string extension)
    {
        var source = CreateSampleVideo();
        var outDir = Path.Combine(_folder, "out-" + extension);
        var result = await _engine.ConvertAsync(source, new(Format(extension), outDir, VideoQuality.Compact));
        Assert.True(File.Exists(result.Output));
        Assert.True(result.Bytes > 0);
        Assert.EndsWith("." + extension, result.Output);
        Assert.Empty(Directory.GetFiles(outDir, ".fluxa-*"));
    }

    [Fact]
    public async Task DuplicateVideoNamesIncrementWithoutOverwriting()
    {
        var source = CreateSampleVideo("clip.mp4");
        var originalBytes = await File.ReadAllBytesAsync(source);
        var first = await _engine.ConvertAsync(source, new(Format("mp4"), _folder, VideoQuality.Compact));
        var second = await _engine.ConvertAsync(source, new(Format("mp4"), _folder, VideoQuality.Compact));
        Assert.EndsWith("clip (1).mp4", first.Output);
        Assert.EndsWith("clip (2).mp4", second.Output);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public void BuildArgumentsReflectsQualityAudioAndMetadataOptions()
    {
        var args = new List<string>();
        VideoConversionEngine.BuildArguments(args, "in.mp4", "out.mp4",
            new(Format("mp4"), _folder, VideoQuality.High, IncludeAudio: false, StripMetadata: true));
        Assert.Contains("-an", args);
        Assert.Contains("-map_metadata", args);
        Assert.Contains("18", args);

        var withAudio = new List<string>();
        VideoConversionEngine.BuildArguments(withAudio, "in.mp4", "out.webm",
            new(Format("webm"), _folder, VideoQuality.Balanced, IncludeAudio: true, StripMetadata: false));
        Assert.DoesNotContain("-an", withAudio);
        Assert.DoesNotContain("-map_metadata", withAudio);
        Assert.Contains("libvpx-vp9", withAudio);
        Assert.Contains("libopus", withAudio);
    }

    [Fact]
    public async Task UnsupportedAndCorruptVideoInputsFailCleanly()
    {
        var txt = Path.Combine(_folder, "notes.txt");
        await File.WriteAllTextAsync(txt, "not a video");
        await Assert.ThrowsAsync<NotSupportedException>(() => _engine.ConvertAsync(txt, new(Format("mp4"), _folder)));

        var corrupt = Path.Combine(_folder, "broken.mp4");
        await File.WriteAllTextAsync(corrupt, "not a real mp4 stream");
        var outDir = Path.Combine(_folder, "corrupt-out");
        await Assert.ThrowsAsync<InvalidDataException>(() => _engine.ConvertAsync(corrupt, new(Format("mp4"), outDir)));
        Assert.Empty(Directory.GetFiles(outDir));
    }

    [Fact]
    public async Task MissingFfmpegGivesActionableMessage()
    {
        var source = CreateSampleVideo();
        var missingEngine = new VideoConversionEngine(Path.Combine(_folder, "nonexistent-ffmpeg.exe"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => missingEngine.ConvertAsync(source, new(Format("mp4"), _folder)));
        Assert.Contains("FFmpeg", ex.Message);
    }

    [Fact]
    public void VideoPreferencesPersistAndValidate()
    {
        var settingsPath = Path.Combine(_folder, "settings.json");
        var fakeExe = Path.Combine(_folder, "ffmpeg.exe");
        File.WriteAllText(fakeExe, "stub");
        new AppSettings
        {
            VideoOutputExtension = "webm",
            VideoQuality = VideoQuality.High,
            VideoIncludeAudio = false,
            VideoStripMetadata = false,
            FfmpegPath = fakeExe
        }.SaveTo(settingsPath);

        var loaded = AppSettings.LoadFrom(settingsPath, settingsPath, out var warning);
        Assert.Null(warning);
        Assert.Equal("webm", loaded.VideoOutputExtension);
        Assert.Equal(VideoQuality.High, loaded.VideoQuality);
        Assert.False(loaded.VideoIncludeAudio);
        Assert.False(loaded.VideoStripMetadata);
        Assert.Equal(fakeExe, loaded.FfmpegPath);

        File.Delete(fakeExe);
        var reloaded = AppSettings.LoadFrom(settingsPath, settingsPath, out _);
        Assert.Null(reloaded.FfmpegPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
