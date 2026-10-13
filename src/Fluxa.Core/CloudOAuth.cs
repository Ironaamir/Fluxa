using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fluxa.Core;

public sealed record OAuthAttempt(string Verifier, string State, Uri AuthorizationUri);

public static class CloudOAuth
{
    public const string DropboxRedirect = "http://127.0.0.1:53682/fluxa/oauth/";
    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static OAuthAttempt CreateAttempt(CloudAppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId)) throw new InvalidOperationException("Configure the " + CloudSources.Name(config.Kind) + " OAuth app in Settings first.");
        ValidateRedirect(config.RedirectUri);
        var verifier = RandomValue();
        var state = RandomValue();
        var values = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId, ["response_type"] = "code", ["redirect_uri"] = config.RedirectUri,
            ["scope"] = CloudSources.Scope(config.Kind), ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256"
        };
        if (config.Kind == FileSourceKind.GoogleDrive) { values["access_type"] = "offline"; values["prompt"] = "consent"; }
        else values["token_access_type"] = "offline";
        return new(verifier, state, new Uri(CloudSources.AuthorizationEndpoint(config.Kind) + "?" + Query(values)));
    }

    public static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public static Uri ValidateRedirect(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port < 1024 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("OAuth callbacks must use an explicit unprivileged port on http://127.0.0.1.");
        return uri;
    }

    public static string ValidateCallback(Uri callback, string redirect, string expectedState)
    {
        var expected = ValidateRedirect(redirect);
        if (callback.Scheme != expected.Scheme || callback.Host != expected.Host || callback.Port != expected.Port || callback.AbsolutePath != expected.AbsolutePath)
            throw new InvalidDataException("Unexpected OAuth callback address.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (!query.TryAdd(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : ""))
                throw new InvalidDataException("Duplicate OAuth callback parameter.");
        }
        if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new InvalidDataException("OAuth state did not match. Please reconnect.");
        if (query.ContainsKey("error")) throw new InvalidOperationException("Sign-in was declined or cancelled. No account was connected.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("OAuth callback has no authorization code.");
        return code;
    }

    public static Task<(string Code, string Verifier, CloudAppConfig Config)> AuthorizeAsync(CloudAppConfig config, CancellationToken cancellationToken)
        => AuthorizeAsync(config, uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), cancellationToken);

    public static async Task<(string Code, string Verifier, CloudAppConfig Config)> AuthorizeAsync(CloudAppConfig config, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var redirect = ValidateRedirect(config.RedirectUri);
        var listener = new TcpListener(IPAddress.Loopback, config.Kind == FileSourceKind.GoogleDrive ? 0 : redirect.Port);
        listener.Start(4);
        try
        {
            if (config.Kind == FileSourceKind.GoogleDrive)
                config = config with { RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{redirect.AbsolutePath}" };
            var attempt = CreateAttempt(config);
            openBrowser(attempt.AuthorizationUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var bytes = new List<byte>();
                var buffer = new byte[1];
                while (bytes.Count < 8192)
                {
                    if (await stream.ReadAsync(buffer, readTimeout.Token) == 0) break;
                    bytes.Add(buffer[0]);
                    if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.ToArray(), bytes.Count - 4, 4) == "\r\n\r\n") break;
                }
                var line = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0].Split(' ');
                var validTarget = line.Length == 3 && line[0] == "GET" && line[1].StartsWith('/') && !line[1].StartsWith("//");
                var callback = validTarget ? new Uri(new Uri(config.RedirectUri), line[1]) : null;
                if (callback == null || callback.AbsolutePath != new Uri(config.RedirectUri).AbsolutePath)
                { await ReplyAsync(stream, false, timeout.Token); continue; }
                try
                {
                    var code = ValidateCallback(callback, config.RedirectUri, attempt.State);
                    await ReplyAsync(stream, true, timeout.Token);
                    return (code, attempt.Verifier, config);
                }
                catch { await ReplyAsync(stream, false, timeout.Token); throw; }
            }
        }
        finally { listener.Stop(); }
    }

    private static Task ReplyAsync(Stream stream, bool success, CancellationToken token)
    {
        var body = success ? "Fluxa sign-in received. You can close this tab and return to Fluxa." : "Fluxa could not accept this sign-in callback. Return to Fluxa and try again.";
        var response = $"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        return stream.WriteAsync(Encoding.UTF8.GetBytes(response), token).AsTask();
    }
}
