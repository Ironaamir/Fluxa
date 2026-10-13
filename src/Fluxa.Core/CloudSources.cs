namespace Fluxa.Core;

public enum FileSourceKind { Local, GoogleDrive, Dropbox }
public sealed record CloudFile(string Id, string Name, long? Size, string? MimeType, bool IsFolder = false, bool CanDownload = true)
{
    public override string ToString() => IsFolder ? "Folder · " + Name : Name + (Size is { } bytes ? $" · {bytes / 1024d:0.#} KB" : "");
}
public sealed record CloudPage(IReadOnlyList<CloudFile> Files, string? Continuation);
public sealed record CloudProviderInfo(FileSourceKind Kind, string Name, bool IsAvailable, string Status);
public sealed record CloudToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string ClientId);
public sealed record CloudAppConfig(FileSourceKind Kind, string ClientId, string RedirectUri);

public interface ICloudTokenStore
{
    CloudToken? Load(FileSourceKind kind);
    void Save(FileSourceKind kind, CloudToken token);
    void Delete(FileSourceKind kind);
}

public interface ICloudFileSource
{
    CloudProviderInfo Info { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<CloudPage> ListFilesAsync(string? parentId, string? continuation, CancellationToken cancellationToken);
    Task DownloadAsync(string fileId, Stream destination, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public static class CloudSources
{
    public static IReadOnlyList<CloudProviderInfo> Providers { get; } =
    [new(FileSourceKind.GoogleDrive, "Google Drive", true, "Read-only image imports"), new(FileSourceKind.Dropbox, "Dropbox", true, "Read-only image imports")];

    public static string Name(FileSourceKind kind) => kind == FileSourceKind.GoogleDrive ? "Google Drive" : "Dropbox";
    public static string Scope(FileSourceKind kind) => kind == FileSourceKind.GoogleDrive
        ? "https://www.googleapis.com/auth/drive.readonly" : "files.metadata.read files.content.read";
    public static string AuthorizationEndpoint(FileSourceKind kind) => kind == FileSourceKind.GoogleDrive
        ? "https://accounts.google.com/o/oauth2/v2/auth" : "https://www.dropbox.com/oauth2/authorize";
    public static string TokenEndpoint(FileSourceKind kind) => kind == FileSourceKind.GoogleDrive
        ? "https://oauth2.googleapis.com/token" : "https://api.dropboxapi.com/oauth2/token";
}
