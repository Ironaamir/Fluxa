namespace Fluxa.Core;

public sealed class CloudImportSession : IDisposable
{
    public const long MaxSessionBytes = 1024L * 1024 * 1024;
    private readonly string _folder;
    private long _bytes;
    public CloudImportSession(string? root = null)
    {
        _folder = Path.Combine(root ?? Path.Combine(Path.GetTempPath(), "Fluxa", "Imports"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }
    public async Task<string> DownloadAsync(ICloudFileSource provider, CloudFile file, CancellationToken token)
    {
        if (file.IsFolder || !file.CanDownload || !ImageConversionEngine.IsSupportedInput(file.Name)) throw new InvalidDataException("Choose a downloadable raster image.");
        if (file.Size > ImageConversionEngine.MaxInputBytes || file.Size > MaxSessionBytes - _bytes) throw new InvalidDataException("Cloud import exceeds the per-file or 1 GB session limit.");
        if (Path.GetFileName(file.Name) != file.Name || file.Name.Contains('/') || file.Name.Contains('\\'))
            throw new InvalidDataException("The cloud image filename contains a path separator.");
        var name = file.Name;
        name = string.Concat(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("The remote image has no usable filename.");
        if (name.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" ||
            Enumerable.Range(1, 9).Any(i => name.Split('.')[0].Equals("COM" + i, StringComparison.OrdinalIgnoreCase) || name.Split('.')[0].Equals("LPT" + i, StringComparison.OrdinalIgnoreCase))) name = "_" + name;
        if (name.Length > 150) name = name[..120] + Path.GetExtension(name);
        var directory = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var limited = new LimitedStream(output, Math.Min(ImageConversionEngine.MaxInputBytes, MaxSessionBytes - _bytes)))
                await provider.DownloadAsync(file.Id, limited, token);
            token.ThrowIfCancellationRequested();
            var length = new FileInfo(path).Length;
            if (file.Size is { } expected && length != expected) throw new InvalidDataException("The cloud download size did not match its metadata. Try again.");
            _bytes += length;
            return path;
        }
        catch { Directory.Delete(directory, true); throw; }
    }
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    private sealed class LimitedStream(Stream inner, long maximum) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        private void Check(int count) { if (_written + count > maximum) throw new InvalidDataException("Cloud download exceeds the file or session limit."); _written += count; }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Check(buffer.Length); await inner.WriteAsync(buffer, token); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    }
}
