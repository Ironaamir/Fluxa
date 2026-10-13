using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Fluxa.Core;

public sealed class CloudFileSource : ICloudFileSource
{
    private readonly CloudAppConfig _config;
    private readonly ICloudTokenStore _store;
    private readonly HttpClient _http;
    private readonly Func<CloudAppConfig, CancellationToken, Task<(string Code, string Verifier, CloudAppConfig Config)>> _authorize;
    private CloudToken? _token;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public CloudProviderInfo Info => new(_config.Kind, CloudSources.Name(_config.Kind), !string.IsNullOrWhiteSpace(_config.ClientId), IsConnected ? "Connected · read-only" : "Not connected");
    public bool IsConnected => _token != null && _token.ClientId == _config.ClientId;

    public CloudFileSource(CloudAppConfig config, ICloudTokenStore store, HttpClient http,
        Func<CloudAppConfig, CancellationToken, Task<(string Code, string Verifier, CloudAppConfig Config)>>? authorize = null)
    {
        _config = config; _store = store; _http = http; _authorize = authorize ?? CloudOAuth.AuthorizeAsync;
        _token = store.Load(config.Kind);
        if (_token?.ClientId != config.ClientId) _token = null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (code, verifier, config) = await _authorize(_config, cancellationToken);
            var fields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = config.ClientId,
                ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = config.RedirectUri };
            await ExchangeAsync(fields, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task ExchangeAsync(Dictionary<string, string> fields, CancellationToken token)
    {
        using var response = await _http.PostAsync(CloudSources.TokenEndpoint(_config.Kind), new FormUrlEncodedContent(fields), token);
        if (!response.IsSuccessStatusCode)
        {
            if (fields["grant_type"] == "refresh_token" && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            { _store.Delete(_config.Kind); _token = null; }
            throw Failure(response, "Sign-in expired, was denied, or app configuration was rejected. Reconnect in Fluxa.");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (root.TryGetProperty("scope", out var scope))
        {
            var granted = scope.GetString()!.Split(' ').ToHashSet();
            if (CloudSources.Scope(_config.Kind).Split(' ').Any(s => !granted.Contains(s)))
                throw new InvalidOperationException("The account did not grant both listing and read-only download permissions. Reconnect and approve the required scopes.");
        }
        var access = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("The provider returned no access token.");
        var refresh = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : _token?.RefreshToken;
        var expires = root.TryGetProperty("expires_in", out var expiresElement) ? expiresElement.GetInt32() : 3600;
        var updated = new CloudToken(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires), _config.ClientId);
        _store.Save(_config.Kind, updated);
        _token = updated;
    }

    private async Task<string> AccessAsync(bool forceRefresh, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!IsConnected) throw new InvalidOperationException("Connect " + Info.Name + " first.");
            if (forceRefresh || _token!.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                if (string.IsNullOrWhiteSpace(_token!.RefreshToken)) throw new InvalidOperationException("The cloud session expired. Reconnect your account.");
                await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = _config.ClientId, ["refresh_token"] = _token.RefreshToken }, token);
            }
            return _token!.AccessToken;
        }
        finally { _gate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync(attempt == 1, token));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { response.Dispose(); continue; }
            if (!response.IsSuccessStatusCode)
            {
                var error = Failure(response, "The cloud request was not accepted. Check account permissions or reconnect.");
                response.Dispose(); throw error;
            }
            return response;
        }
        throw new InvalidOperationException("The cloud account requires reconnection.");
    }

    private Exception Failure(HttpResponseMessage response, string message) => new InvalidOperationException(
        Info.Name + $": HTTP {(int)response.StatusCode}. " + (response.StatusCode == HttpStatusCode.TooManyRequests ? "Rate limit reached. Wait before retrying." : message));

    public async Task<CloudPage> ListFilesAsync(string? parentId, string? continuation, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => ListRequest(parentId, continuation), cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var files = new List<CloudFile>();
        if (_config.Kind == FileSourceKind.GoogleDrive)
        {
            if (root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean())
                throw new InvalidOperationException("Drive returned an incomplete listing. Try a specific folder.");
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                var mime = item.GetProperty("mimeType").GetString();
                long? size = item.TryGetProperty("size", out var sizeValue) && long.TryParse(sizeValue.GetString(), out var bytes) ? bytes : null;
                var folder = mime == "application/vnd.google-apps.folder";
                var canDownload = !item.TryGetProperty("capabilities", out var caps) || !caps.TryGetProperty("canDownload", out var can) || can.GetBoolean();
                files.Add(new(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!, size, mime, folder, canDownload));
            }
            return new(files, root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null);
        }
        foreach (var item in root.GetProperty("entries").EnumerateArray())
        {
            var tag = item.GetProperty(".tag").GetString();
            if (tag is not ("file" or "folder")) continue;
            files.Add(new(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!,
                item.TryGetProperty("size", out var size) ? size.GetInt64() : null, null, tag == "folder",
                !item.TryGetProperty("is_downloadable", out var downloadable) || downloadable.GetBoolean()));
        }
        return new(files, root.GetProperty("has_more").GetBoolean() ? root.GetProperty("cursor").GetString() : null);
    }

    private HttpRequestMessage ListRequest(string? parent, string? continuation)
    {
        if (_config.Kind == FileSourceKind.GoogleDrive)
        {
            var escapedParent = (parent ?? "root").Replace("\\", "\\\\").Replace("'", "\\'");
            var values = new Dictionary<string, string> { ["q"] = $"'{escapedParent}' in parents and trashed = false",
                ["fields"] = "nextPageToken,incompleteSearch,files(id,name,mimeType,size,capabilities(canDownload))", ["pageSize"] = "100",
                ["orderBy"] = "folder,name", ["supportsAllDrives"] = "true", ["includeItemsFromAllDrives"] = "true" };
            if (continuation != null) values["pageToken"] = continuation;
            return new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?" + CloudOAuth.Query(values));
        }
        return JsonRequest(continuation == null ? "files/list_folder" : "files/list_folder/continue",
            continuation == null ? new { path = parent ?? "", recursive = false, include_deleted = false, limit = 100 } : (object)new { cursor = continuation });
    }

    private static HttpRequestMessage JsonRequest(string route, object body) => new(HttpMethod.Post, "https://api.dropboxapi.com/2/" + route)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public async Task DownloadAsync(string fileId, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            if (_config.Kind == FileSourceKind.GoogleDrive)
                return new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId) + "?alt=media&supportsAllDrives=true");
            var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = fileId }));
            return request;
        }, cancellationToken);
        if (response.Content.Headers.ContentLength > ImageConversionEngine.MaxInputBytes) throw new InvalidDataException("Cloud image exceeds the 512 MB input limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > ImageConversionEngine.MaxInputBytes) throw new InvalidDataException("Cloud image exceeds the 512 MB input limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsConnected) return;
            if (_config.Kind == FileSourceKind.GoogleDrive)
            {
                using var response = await _http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["token"] = _token!.RefreshToken ?? _token.AccessToken }), cancellationToken);
                if (!response.IsSuccessStatusCode) throw Failure(response, "Local credentials were removed, but revoke the app's access from your Google account as well.");
            }
            else
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/auth/token/revoke"), cancellationToken);
            }
        }
        finally { _token = null; _store.Delete(_config.Kind); }
    }
}
