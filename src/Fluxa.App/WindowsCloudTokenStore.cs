using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fluxa.Core;

namespace Fluxa.App;

internal sealed class WindowsCloudTokenStore(string? directory = null) : ICloudTokenStore
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluxa", "Connections");
    private string FileFor(FileSourceKind kind) => Path.Combine(_directory, kind + ".bin");
    public CloudToken? Load(FileSourceKind kind)
    {
        var path = FileFor(kind);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = Transform(File.ReadAllBytes(path), protect: false);
            try { return JsonSerializer.Deserialize<CloudToken>(bytes); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException)
        { throw new InvalidDataException("Saved " + CloudSources.Name(kind) + " credentials could not be unlocked. Disconnect and reconnect that account.", ex); }
    }
    public void Save(FileSourceKind kind, CloudToken token)
    {
        Directory.CreateDirectory(_directory);
        var path = FileFor(kind);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(token);
        try
        {
            var encrypted = Transform(bytes, protect: true);
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(FileSourceKind kind)
    {
        var path = FileFor(kind);
        if (File.Exists(path)) File.Delete(path);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var blob = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Marshal.Copy(input, 0, blob.Data, input.Length);
        Blob output = default;
        try
        {
            var ok = protect ? CryptProtectData(ref blob, "Fluxa cloud connection", 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref blob, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            for (var i = 0; i < blob.Size; i++) Marshal.WriteByte(blob.Data, i, 0);
            Marshal.FreeHGlobal(blob.Data);
            if (output.Data != 0)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
