using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace SpotifyRadio;

public sealed class Tokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Authorization Code + PKCE flow. No client secret; tokens are kept in tokens.json.</summary>
public sealed class SpotifyAuth
{
    private const string Scopes =
        "user-read-playback-state user-modify-playback-state user-read-currently-playing " +
        "playlist-read-private playlist-read-collaborative";

    private readonly Config _cfg;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Tokens? _tokens;

    public SpotifyAuth(Config cfg, HttpClient http)
    {
        _cfg = cfg;
        _http = http;
        if (File.Exists(Config.TokensPath))
        {
            try { _tokens = JsonSerializer.Deserialize<Tokens>(File.ReadAllText(Config.TokensPath), Config.Json); }
            catch (JsonException) { _tokens = null; }
        }
    }

    /// <summary>Run once at startup: refreshes the saved login, or opens the browser for a new one.</summary>
    public async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_tokens is { RefreshToken.Length: > 0 })
            {
                if (_tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return;
                if (await RefreshAsync(ct)) return;
                Log.Info("Saved login is no longer valid, logging in again.");
            }
            await LoginInteractiveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_tokens is null || _tokens.AccessToken.Length == 0)
                throw new InvalidOperationException("Not logged in to Spotify - restart the daemon");
            if (_tokens.ExpiresAtUtc <= DateTime.UtcNow.AddSeconds(60) && !await RefreshAsync(ct))
                throw new InvalidOperationException("Spotify login expired - restart the daemon");
            return _tokens.AccessToken;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Called after a 401 from the API.</summary>
    public async Task<bool> ForceRefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await RefreshAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_tokens is null || _tokens.RefreshToken.Length == 0) return false;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _tokens.RefreshToken,
            ["client_id"] = _cfg.ClientId,
        };
        return await RequestTokenAsync(form, ct);
    }

    private async Task<bool> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(_cfg.AccountsBase + "/api/token", new FormUrlEncodedContent(form), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Info($"Token request failed ({(int)resp.StatusCode}): {raw}");
            return false;
        }
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var next = new Tokens
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            // Spotify may or may not rotate the refresh token; keep the old one if none is returned.
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()!
                : _tokens?.RefreshToken ?? "",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600),
        };
        _tokens = next;
        File.WriteAllText(Config.TokensPath, JsonSerializer.Serialize(next, Config.Json));
        return true;
    }

    private async Task LoginInteractiveAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var url = _cfg.AccountsBase + "/authorize" +
                  "?client_id=" + Uri.EscapeDataString(_cfg.ClientId) +
                  "&response_type=code" +
                  "&redirect_uri=" + Uri.EscapeDataString(_cfg.RedirectUri) +
                  "&code_challenge_method=S256" +
                  "&code_challenge=" + challenge +
                  "&state=" + state +
                  "&scope=" + Uri.EscapeDataString(Scopes);

        var listener = new TcpListener(IPAddress.Loopback, _cfg.AuthPort);
        listener.Start();
        try
        {
            Log.Info("Opening the Spotify login page. If the browser does not open, paste this URL:");
            Console.WriteLine(url);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Log.Info("Could not open the browser: " + e.Message); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var code = await WaitForCodeAsync(listener, state, timeout.Token);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _cfg.RedirectUri,
                ["client_id"] = _cfg.ClientId,
                ["code_verifier"] = verifier,
            };
            if (!await RequestTokenAsync(form, ct))
                throw new InvalidOperationException("Spotify rejected the login. Check the Client ID and redirect URI.");
            Log.Info("Logged in to Spotify.");
        }
        finally { listener.Stop(); }
    }

    /// <summary>Tiny one-shot HTTP server for the OAuth redirect (no HttpListener, so no URL ACLs needed).</summary>
    private static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            var parts = requestLine.Split(' ');
            var target = parts.Length > 1 ? parts[1] : "";

            string? code = null, error = null;
            var isCallback = target.StartsWith("/callback", StringComparison.Ordinal);
            if (isCallback)
            {
                var q = target.IndexOf('?');
                var query = HttpUtility.ParseQueryString(q >= 0 ? target[(q + 1)..] : "");
                if (query["state"] != expectedState) error = "state mismatch";
                else if (query["error"] is { } e) error = e;
                else code = query["code"];
            }

            var body = !isCallback ? "Not found"
                : code is not null ? "<html><body style='font-family:sans-serif'><h2>Spotify Radio is connected.</h2>You can close this tab and start the game.</body></html>"
                : $"<html><body style='font-family:sans-serif'><h2>Login failed</h2>{WebUtility.HtmlEncode(error ?? "no code")}</body></html>";
            var status = isCallback ? "200 OK" : "404 Not Found";
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);

            if (code is not null) return code;
            if (isCallback) throw new InvalidOperationException("Spotify login failed: " + (error ?? "no code returned"));
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
