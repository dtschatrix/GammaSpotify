using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpotifyRadio;

public sealed record ApiResult(int Status, JsonElement? Json, string Raw)
{
    public bool Ok => Status is >= 200 and < 300;

    private JsonElement? Error =>
        Json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("error", out var e) ? e : null;

    public string Reason => Error is { ValueKind: JsonValueKind.Object } e ? J.Str(e, "reason") : "";

    public string Message
    {
        get
        {
            if (Error is { ValueKind: JsonValueKind.Object } e && J.Str(e, "message") is { Length: > 0 } m) return m;
            if (Error is { ValueKind: JsonValueKind.String } s) return s.GetString() ?? "";
            return $"HTTP {Status}";
        }
    }
}

public sealed record PlayerState(
    string Status, string Artist, string Title, string Uri,
    int ProgressMs, int DurationMs, int Volume, string Device, bool Shuffle)
{
    public static readonly PlayerState NoDevice = new("nodevice", "", "", "", 0, 0, -1, "", false);

    /// <summary>What has to change before the game is told again.</summary>
    public string Signature => $"{Status}|{Uri}|{Volume}|{Device}|{Shuffle}";
}

public sealed record DeviceInfo(string Id, string Name, bool Active);

public sealed record SearchItem(string Kind, string Uri, string Title, string Subtitle);

/// <summary>Raised for anything the player in game should see as a one-line message.</summary>
public sealed class SpotifyException : Exception
{
    public SpotifyException(string message) : base(message) { }
}

internal static class J
{
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public static int Int(JsonElement e, string name, int fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : fallback;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static IEnumerable<JsonElement> Arr(JsonElement? e, string name)
    {
        if (e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) // Spotify returns null entries in some lists
                    yield return item;
    }
}

public sealed class SpotifyClient
{
    private readonly Config _cfg;
    private readonly SpotifyAuth _auth;
    private readonly HttpClient _http;

    public SpotifyClient(Config cfg, SpotifyAuth auth, HttpClient http)
    {
        _cfg = cfg;
        _auth = auth;
        _http = http;
    }

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(ct);
            using var req = new HttpRequestMessage(method, _cfg.ApiBase + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            else if (method != HttpMethod.Get)
                req.Content = new ByteArrayContent(Array.Empty<byte>()); // Spotify wants Content-Length: 0

            using var resp = await _http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (status == 401 && attempt == 0 && await _auth.ForceRefreshAsync(ct)) continue;
            if (status == 429 && attempt == 0)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                if (wait <= TimeSpan.FromSeconds(5)) { await Task.Delay(wait, ct); continue; }
                throw new SpotifyException($"Spotify rate limit, retry in {(int)wait.TotalSeconds}s");
            }

            JsonElement? json = null;
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    json = doc.RootElement.Clone();
                }
                catch (JsonException) { /* some player endpoints return non-JSON bodies */ }
            }
            return new ApiResult(status, json, raw);
        }
    }

    public async Task<PlayerState> GetStateAsync(CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, "/me/player", null, ct);
        if (r.Status == 204 || (r.Ok && r.Json is null)) return PlayerState.NoDevice;
        if (!r.Ok) throw new SpotifyException(Friendly(r));

        var root = r.Json!.Value;
        var device = J.Obj(root, "device");
        var item = J.Obj(root, "item");
        var deviceName = device is { } d ? J.Str(d, "name") : "";
        var volume = device is { } d2 ? J.Int(d2, "volume_percent", -1) : -1;
        var shuffle = J.Bool(root, "shuffle_state");
        if (item is not { } it)
            return new PlayerState("idle", "", "", "", 0, 0, volume, deviceName, shuffle);

        var artists = string.Join(", ", J.Arr(it, "artists").Select(a => J.Str(a, "name")).Where(n => n.Length > 0));
        if (artists.Length == 0 && J.Obj(it, "show") is { } show) artists = J.Str(show, "name"); // podcast episode
        return new PlayerState(
            J.Bool(root, "is_playing") ? "playing" : "paused",
            artists, J.Str(it, "name"), J.Str(it, "uri"),
            J.Int(root, "progress_ms"), J.Int(it, "duration_ms"), volume, deviceName, shuffle);
    }

    /// <summary>Sends a player command; if nothing is active, wakes a device and retries once.</summary>
    private async Task PlayerCommandAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        bool ignoreRestriction = false)
    {
        var r = await SendAsync(method, path, body, ct);
        if (r.Status == 404 || r.Reason == "NO_ACTIVE_DEVICE")
        {
            var deviceId = await PickDeviceAsync(ct)
                ?? throw new SpotifyException("No Spotify device found - open the Spotify app");
            var sep = path.Contains('?') ? '&' : '?';
            r = await SendAsync(method, $"{path}{sep}device_id={Uri.EscapeDataString(deviceId)}", body, ct);
        }
        // "Restriction violated" is what Spotify answers to play-while-playing / pause-while-paused.
        if (ignoreRestriction && r.Status == 403 && r.Message.Contains("Restriction", StringComparison.OrdinalIgnoreCase)) return;
        if (!r.Ok) throw new SpotifyException(Friendly(r));
    }

    private async Task<string?> PickDeviceAsync(CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, "/me/player/devices", null, ct);
        if (!r.Ok) throw new SpotifyException(Friendly(r));
        // The in-game audio device is only used when the game asks for it, never as a fallback.
        var devices = J.Arr(r.Json, "devices")
            .Where(d => !J.Bool(d, "is_restricted") && J.Str(d, "name") != _cfg.StreamDeviceName)
            .ToList();
        if (devices.Count == 0) return null;

        JsonElement? pick = null;
        if (_cfg.PreferredDevice.Length > 0)
            pick = devices.Cast<JsonElement?>().FirstOrDefault(d =>
                J.Str(d!.Value, "name").Contains(_cfg.PreferredDevice, StringComparison.OrdinalIgnoreCase));
        pick ??= devices.Cast<JsonElement?>().FirstOrDefault(d =>
            J.Str(d!.Value, "name").Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        pick ??= devices.Cast<JsonElement?>().FirstOrDefault(d => J.Str(d!.Value, "type") == "Computer");
        pick ??= devices[0];
        Log.Info($"No active device, using \"{J.Str(pick.Value, "name")}\".");
        return J.Str(pick.Value, "id");
    }

    private static string Friendly(ApiResult r) => r.Reason switch
    {
        "PREMIUM_REQUIRED" => "Spotify Premium is required for playback control",
        "NO_ACTIVE_DEVICE" => "No Spotify device found - open the Spotify app",
        _ => r.Status == 403 && r.Message.Contains("premium", StringComparison.OrdinalIgnoreCase)
            ? "Spotify Premium is required for playback control"
            : "Spotify: " + r.Message,
    };

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, "/me/player/devices", null, ct);
        if (!r.Ok) throw new SpotifyException(Friendly(r));
        return J.Arr(r.Json, "devices")
            .Select(d => new DeviceInfo(J.Str(d, "id"), J.Str(d, "name"), J.Bool(d, "is_active")))
            .Where(d => d.Id.Length > 0)
            .ToList();
    }

    /// <summary>Moves playback to another device. play=false keeps the current play/pause state.</summary>
    public async Task TransferAsync(string deviceId, bool play, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["device_ids"] = new[] { deviceId }, ["play"] = play };
        var r = await SendAsync(HttpMethod.Put, "/me/player", body, ct);
        if (!r.Ok) throw new SpotifyException(Friendly(r));
    }

    public Task PlayAsync(CancellationToken ct) => PlayerCommandAsync(HttpMethod.Put, "/me/player/play", null, ct, ignoreRestriction: true);
    public Task PauseAsync(CancellationToken ct) => PlayerCommandAsync(HttpMethod.Put, "/me/player/pause", null, ct, ignoreRestriction: true);
    public Task NextAsync(CancellationToken ct) => PlayerCommandAsync(HttpMethod.Post, "/me/player/next", null, ct);
    public Task PreviousAsync(CancellationToken ct) => PlayerCommandAsync(HttpMethod.Post, "/me/player/previous", null, ct);

    public Task SetVolumeAsync(int percent, CancellationToken ct) =>
        PlayerCommandAsync(HttpMethod.Put, $"/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}", null, ct);

    public Task SetShuffleAsync(bool on, CancellationToken ct) =>
        PlayerCommandAsync(HttpMethod.Put, $"/me/player/shuffle?state={(on ? "true" : "false")}", null, ct);

    public Task QueueAsync(string uri, CancellationToken ct) =>
        PlayerCommandAsync(HttpMethod.Post, $"/me/player/queue?uri={Uri.EscapeDataString(uri)}", null, ct);

    public Task PlayUriAsync(string uri, CancellationToken ct)
    {
        if (!uri.StartsWith("spotify:", StringComparison.Ordinal))
            throw new SpotifyException("Not a Spotify URI: " + uri);
        var single = uri.StartsWith("spotify:track:", StringComparison.Ordinal) ||
                     uri.StartsWith("spotify:episode:", StringComparison.Ordinal);
        object body = single
            ? new Dictionary<string, object> { ["uris"] = new[] { uri } }
            : new Dictionary<string, object> { ["context_uri"] = uri };
        return PlayerCommandAsync(HttpMethod.Put, "/me/player/play", body, ct);
    }

    public async Task<List<SearchItem>> SearchAsync(string query, CancellationToken ct)
    {
        var limit = Math.Clamp(_cfg.SearchLimit, 1, 10);
        var r = await SendAsync(HttpMethod.Get,
            $"/search?q={Uri.EscapeDataString(query)}&type=track,playlist,album&limit={limit}", null, ct);
        if (!r.Ok) throw new SpotifyException(Friendly(r));
        var root = r.Json ?? default;

        var items = new List<SearchItem>();
        foreach (var t in J.Arr(J.Obj(root, "tracks"), "items"))
            items.Add(new SearchItem("track", J.Str(t, "uri"), J.Str(t, "name"), Artists(t)));
        foreach (var p in J.Arr(J.Obj(root, "playlists"), "items"))
            items.Add(new SearchItem("playlist", J.Str(p, "uri"), J.Str(p, "name"), Owner(p)));
        foreach (var a in J.Arr(J.Obj(root, "albums"), "items"))
            items.Add(new SearchItem("album", J.Str(a, "uri"), J.Str(a, "name"), Artists(a)));
        return items.Where(i => i.Uri.Length > 0).ToList();
    }

    public async Task<List<SearchItem>> MyPlaylistsAsync(CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, "/me/playlists?limit=50", null, ct);
        if (!r.Ok) throw new SpotifyException(Friendly(r));
        return J.Arr(r.Json, "items")
            .Select(p => new SearchItem("playlist", J.Str(p, "uri"), J.Str(p, "name"), Owner(p)))
            .Where(i => i.Uri.Length > 0)
            .ToList();
    }

    private static string Artists(JsonElement e) =>
        string.Join(", ", J.Arr(e, "artists").Select(a => J.Str(a, "name")).Where(n => n.Length > 0));

    private static string Owner(JsonElement e) => J.Obj(e, "owner") is { } o ? J.Str(o, "display_name") : "";
}
