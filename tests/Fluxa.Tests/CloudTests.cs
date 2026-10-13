using System.Net;
using System.Text;
using System.Text.Json;
using Fluxa.Core;
using Xunit;

namespace Fluxa.Tests;

public sealed class CloudTests
{
    private sealed class Tokens : ICloudTokenStore
    {
        public CloudToken? Value = new("access-test", "refresh-test", DateTimeOffset.UtcNow.AddHours(1), "client-test");
        public CloudToken? Load(FileSourceKind kind) => Value;
        public void Save(FileSourceKind kind, CloudToken token) => Value = token;
        public void Delete(FileSourceKind kind) => Value = null;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return handle(request); }
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value) };
    private static CloudAppConfig Config(FileSourceKind kind) => new(kind, "client-test", CloudOAuth.DropboxRedirect);

    [Theory]
    [InlineData(FileSourceKind.GoogleDrive)]
    [InlineData(FileSourceKind.Dropbox)]
    public void OAuthUsesPkceReadOnlyScopesFreshStateAndLoopback(FileSourceKind kind)
    {
        var a = CloudOAuth.CreateAttempt(Config(kind)); var b = CloudOAuth.CreateAttempt(Config(kind));
        Assert.NotEqual(a.State, b.State); Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.Equal(43, a.Verifier.Length);
        Assert.Contains("code_challenge_method=S256", a.AuthorizationUri.Query);
        Assert.Contains(Uri.EscapeDataString(CloudSources.Scope(kind)), a.AuthorizationUri.Query);
        Assert.Equal("abc+def", CloudOAuth.ValidateCallback(new Uri(CloudOAuth.DropboxRedirect + "?code=abc%2Bdef&state=" + a.State), CloudOAuth.DropboxRedirect, a.State));
        Assert.Throws<InvalidDataException>(() => CloudOAuth.ValidateCallback(new Uri(CloudOAuth.DropboxRedirect + "?code=abc&state=wrong"), CloudOAuth.DropboxRedirect, a.State));
        Assert.Throws<InvalidDataException>(() => CloudOAuth.ValidateCallback(new Uri(CloudOAuth.DropboxRedirect + "?code=abc&state=" + a.State + "&state=" + a.State), CloudOAuth.DropboxRedirect, a.State));
        Assert.Throws<InvalidOperationException>(() => CloudOAuth.ValidateCallback(new Uri(CloudOAuth.DropboxRedirect + "?error=access_denied&state=" + a.State), CloudOAuth.DropboxRedirect, a.State));
        Assert.Throws<ArgumentException>(() => CloudOAuth.ValidateRedirect("http://0.0.0.0:53682/"));
        Assert.Throws<ArgumentException>(() => CloudOAuth.ValidateRedirect("https://example.com/"));
    }

    [Fact]
    public async Task ActualLoopbackListenerAcceptsValidCallbackAndSupportsCancellation()
    {
        var callbackSent = new TaskCompletionSource<Task<HttpResponseMessage>>();
        using var http = new HttpClient();
        var result = await CloudOAuth.AuthorizeAsync(Config(FileSourceKind.GoogleDrive), uri =>
        {
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            callbackSent.SetResult(http.GetAsync(query["redirect_uri"] + "?code=loopback-code&state=" + query["state"]));
        }, default);
        using var response = await await callbackSent.Task;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("loopback-code", result.Code);
        Assert.StartsWith("http://127.0.0.1:", result.Config.RedirectUri);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudOAuth.AuthorizeAsync(Config(FileSourceKind.GoogleDrive), _ => { }, cts.Token));
    }

    [Fact]
    public async Task DriveListsFolderPagesWithOpaqueIdsAndBinaryDownload()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("Bearer access-test", request.Headers.Authorization!.ToString());
            calls++;
            if (request.RequestUri!.Query.Contains("alt=media"))
            {
                Assert.Contains("files/opaque%2Fid", request.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            }
            Assert.Contains("supportsAllDrives=true", request.RequestUri.Query);
            Assert.Contains("in%20parents", request.RequestUri.Query);
            if (calls == 2) Assert.Contains("pageToken=next", request.RequestUri.Query);
            return Task.FromResult(Json("{\"files\":[{\"id\":\"folder\",\"name\":\"Photos\",\"mimeType\":\"application/vnd.google-apps.folder\"},{\"id\":\"opaque/id\",\"name\":\"photo.png\",\"size\":\"3\",\"mimeType\":\"image/png\",\"capabilities\":{\"canDownload\":true}}],\"nextPageToken\":\"next\"}"));
        }));
        var provider = new CloudFileSource(Config(FileSourceKind.GoogleDrive), new Tokens(), http);
        var page = await provider.ListFilesAsync(null, null, default);
        Assert.True(page.Files[0].IsFolder); Assert.Equal(3, page.Files[1].Size); Assert.Equal("next", page.Continuation);
        await provider.ListFilesAsync("folder", page.Continuation, default);
        using var download = new MemoryStream(); await provider.DownloadAsync("opaque/id", download, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, download.ToArray());
    }

    [Fact]
    public async Task DropboxListsRootAndContinuationAndUsesDownloadArgumentHeader()
    {
        var call = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer access-test", request.Headers.Authorization!.ToString());
            call++;
            if (call == 3)
            {
                Assert.Equal("content.dropboxapi.com", request.RequestUri!.Host);
                Assert.Equal("id:opaque", JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single()).RootElement.GetProperty("path").GetString());
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5]) };
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (call == 1) Assert.Equal("", body.RootElement.GetProperty("path").GetString());
            else { Assert.EndsWith("/continue", request.RequestUri!.AbsolutePath); Assert.Equal("cursor-test", body.RootElement.GetProperty("cursor").GetString()); }
            return Json("{\"entries\":[{\".tag\":\"folder\",\"id\":\"id:folder\",\"name\":\"Photos\"},{\".tag\":\"file\",\"id\":\"id:opaque\",\"name\":\"photo.png\",\"size\":2}],\"has_more\":" + (call == 1 ? "true" : "false") + ",\"cursor\":\"cursor-test\"}");
        }));
        var provider = new CloudFileSource(Config(FileSourceKind.Dropbox), new Tokens(), http);
        var page = await provider.ListFilesAsync(null, null, default);
        Assert.True(page.Files[0].IsFolder); Assert.Equal("cursor-test", page.Continuation);
        Assert.Null((await provider.ListFilesAsync("id:folder", page.Continuation, default)).Continuation);
        using var output = new MemoryStream(); await provider.DownloadAsync("id:opaque", output, default);
        Assert.Equal(new byte[] { 4, 5 }, output.ToArray());
    }

    [Theory]
    [InlineData(FileSourceKind.GoogleDrive)]
    [InlineData(FileSourceKind.Dropbox)]
    public async Task ExpiredTokensRefreshAndPreserveRefreshToken(FileSourceKind kind)
    {
        var store = new Tokens { Value = new("old", "refresh-test", DateTimeOffset.UtcNow.AddMinutes(-1), "client-test") };
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=refresh_token", body); Assert.DoesNotContain("client_secret", body);
                return Json("{\"access_token\":\"new\",\"expires_in\":3600}");
            }
            Assert.Equal("Bearer new", request.Headers.Authorization!.ToString());
            return kind == FileSourceKind.GoogleDrive ? Json("{\"files\":[]}") : Json("{\"entries\":[],\"has_more\":false,\"cursor\":\"x\"}");
        }));
        var provider = new CloudFileSource(Config(kind), store, http);
        await provider.ListFilesAsync(null, null, default);
        Assert.Equal("new", store.Value!.AccessToken); Assert.Equal("refresh-test", store.Value.RefreshToken);
    }

    [Fact]
    public async Task UnauthorizedRequestsRefreshOnceAndOversizedDownloadsAreRejected()
    {
        var store = new Tokens(); var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token")) return Task.FromResult(Json("{\"access_token\":\"updated\",\"expires_in\":3600}"));
            if (++calls == 1) return Task.FromResult(Json("denied", HttpStatusCode.Unauthorized));
            Assert.Equal("Bearer updated", request.Headers.Authorization!.ToString());
            var content = new ByteArrayContent([]); content.Headers.ContentLength = ImageConversionEngine.MaxInputBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var provider = new CloudFileSource(Config(FileSourceKind.Dropbox), store, http);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.DownloadAsync("id:large", output, default));
        Assert.Equal(2, calls); Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CloudDownloadCanEnterTheRealImageConversionEngineWithoutAlteringTheSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "Fluxa.CloudConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes;
            using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Blue, 12, 10)) bytes = image.ToByteArray(ImageMagick.MagickFormat.Png);
            using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            var provider = new CloudFileSource(Config(FileSourceKind.GoogleDrive), new Tokens(), http);
            using var session = new CloudImportSession(Path.Combine(root, "imports"));
            var local = await session.DownloadAsync(provider, new("remote", "picture.png", bytes.Length, "image/png"), default);
            var result = await new ImageConversionEngine().ConvertAsync(local, new(ImageConversionEngine.OutputFormats.First(f => f.Extension == "jpg"), Path.Combine(root, "output")));
            using var decoded = new ImageMagick.MagickImage(result.Output);
            Assert.Equal(12u, decoded.Width); Assert.Equal(10u, decoded.Height); Assert.Equal(bytes, File.ReadAllBytes(local));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidRefreshClearsConnectionAndProviderErrorsDoNotLeakTokens()
    {
        var store = new Tokens { Value = new("SECRET", "SECRET-REFRESH", DateTimeOffset.UtcNow.AddHours(-1), "client-test") };
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("SECRET-REFRESH", HttpStatusCode.BadRequest))));
        var provider = new CloudFileSource(Config(FileSourceKind.GoogleDrive), store, http);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListFilesAsync(null, null, default));
        Assert.DoesNotContain("SECRET", ex.Message); Assert.Null(store.Value); Assert.False(provider.IsConnected);
    }

    [Theory]
    [InlineData(FileSourceKind.GoogleDrive)]
    [InlineData(FileSourceKind.Dropbox)]
    public async Task ConnectExchangesPkceAndDisconnectRemovesSavedTokensEvenWhenRevokeFails(FileSourceKind kind)
    {
        var store = new Tokens { Value = null };
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                var body = await request.Content!.ReadAsStringAsync(); Assert.Contains("code_verifier=verifier", body);
                Assert.Contains("redirect_uri=", body); Assert.DoesNotContain("client_secret", body);
                return Json("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"scope\":\"" + CloudSources.Scope(kind) + "\"}");
            }
            return Json("not revoked", HttpStatusCode.Forbidden);
        }));
        var provider = new CloudFileSource(Config(kind), store, http, (config, token) => Task.FromResult(("code", "verifier", config)));
        await provider.ConnectAsync(default); Assert.True(provider.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DisconnectAsync(default));
        Assert.Null(store.Value); Assert.False(provider.IsConnected);
    }

    [Fact]
    public void WindowsTokenStorageIsEncryptedAndSurvivesReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "Fluxa.TokenTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Fluxa.App.WindowsCloudTokenStore(root);
            store.Delete(FileSourceKind.GoogleDrive); // First use: even the directory is absent.
            var token = new CloudToken("sensitive-access-fixture", "sensitive-refresh-fixture", DateTimeOffset.UtcNow.AddHours(1), "public-client");
            store.Save(FileSourceKind.GoogleDrive, token);
            var bytes = File.ReadAllBytes(Path.Combine(root, "GoogleDrive.bin"));
            Assert.DoesNotContain("sensitive", Encoding.UTF8.GetString(bytes));
            Assert.Equal(token, new Fluxa.App.WindowsCloudTokenStore(root).Load(FileSourceKind.GoogleDrive));
            store.Delete(FileSourceKind.GoogleDrive); Assert.Null(store.Load(FileSourceKind.GoogleDrive));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeSource : ICloudFileSource
    {
        public CloudProviderInfo Info => new(FileSourceKind.Dropbox, "Dropbox", true, "test");
        public bool IsConnected => true;
        public bool Fail;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<CloudPage> ListFilesAsync(string? parent, string? page, CancellationToken token) => Task.FromResult(new CloudPage([], null));
        public async Task DownloadAsync(string id, Stream output, CancellationToken token) { await output.WriteAsync(new byte[] { 1, 2, 3 }, token); if (Fail) throw new IOException("interrupted"); }
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task ImportsKeepSafeNamesCleanPartialsAndEnforceMetadataAndCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Fluxa.CloudTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var session = new CloudImportSession(root))
            {
                var source = new FakeSource();
                await Assert.ThrowsAsync<InvalidDataException>(() => session.DownloadAsync(source, new("opaque", "../CON.png", 3, null), default));
                var path = await session.DownloadAsync(source, new("opaque", "CON.png", 3, null), default);
                Assert.Equal("_CON.png", Path.GetFileName(path)); Assert.StartsWith(root, path); Assert.Equal(3, new FileInfo(path).Length);
                source.Fail = true;
                await Assert.ThrowsAsync<IOException>(() => session.DownloadAsync(source, new("other", "bad.png", null, null), default));
                Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
                source.Fail = false;
                await Assert.ThrowsAsync<InvalidDataException>(() => session.DownloadAsync(source, new("other", "size.png", 99, null), default));
                await Assert.ThrowsAsync<InvalidDataException>(() => session.DownloadAsync(source, new("other", "huge.png", ImageConversionEngine.MaxInputBytes + 1, null), default));
                using var cts = new CancellationTokenSource(); cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DownloadAsync(source, new("other", "cancel.png", null, null), cts.Token));
                Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            }
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
