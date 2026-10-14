using System.Text.Json;

namespace SpotifyRadio;

public sealed class Config
{
    /// <summary>What plays in the game: "spotify", "youtube" or both. Asked on the first run; remove it to be asked again.</summary>
    public string[]? Sources { get; set; }
    /// <summary>Client ID of your app at developer.spotify.com/dashboard (Spotify only).</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Port the game connects to (must match PORT in spotify_radio.script).</summary>
    public int GamePort { get; set; } = 47800;
    /// <summary>Port of the one-time login callback: http://127.0.0.1:{AuthPort}/callback</summary>
    public int AuthPort { get; set; } = 47801;
    /// <summary>The game's appdata folder (e.g. D:\\Games\\Anomaly\\appdata). Commands and replies go through two text files there.</summary>
    public string IpcDir { get; set; } = "";
    /// <summary>Path to librespot. Empty = librespot\librespot.exe next to run.bat. Without it only the remote control works.</summary>
    public string LibrespotPath { get; set; } = "";
    /// <summary>Replaces the whole librespot command line (testing only).</summary>
    public string[]? LibrespotArgs { get; set; }
    /// <summary>Name of the Spotify device that plays through the game.</summary>
    public string StreamDeviceName { get; set; } = "STALKER PDA";
    /// <summary>Length of each audio chunk handed to the game. The game script expects 1000; see spotify_radio_stream.script before changing it.</summary>
    public int ChunkMs { get; set; } = 1000;
    /// <summary>Seconds without a word from the game (paused, main menu, loading, closed) before Spotify is paused. It resumes when the game is back.</summary>
    public int AwaySeconds { get; set; } = 3;
    /// <summary>How much audio the game is kept supplied with, in ms. It needs five seconds in hand to start a sound; reading Spotify ahead of real time fills that in a moment.</summary>
    public int LeadMs { get; set; } = 6000;
    /// <summary>Volume of the in-game sound, 0..100, set with the PDA's volume buttons.</summary>
    public int GameVolume { get; set; } = 100;
    /// <summary>Text sent to the game: "ascii" (transliterated), "windows-1251" or "utf-8".</summary>
    public string Encoding { get; set; } = "ascii";
    /// <summary>Part of the device name to prefer when nothing is active. Empty = this PC, else first found.</summary>
    public string PreferredDevice { get; set; } = "";
    /// <summary>Results per type (tracks/playlists/albums). Spotify caps this at 10.</summary>
    public int SearchLimit { get; set; } = 10;
    public int PollSeconds { get; set; } = 2;
    public string ApiBase { get; set; } = "https://api.spotify.com/v1";
    public string AccountsBase { get; set; } = "https://accounts.spotify.com";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// The daemon's own folder: the one holding run.bat, the librespot folder and maybe a config.json.
    /// Normally the working directory, but the exe can also be started from elsewhere or from
    /// bin\Release\..., so the exe's folder and its parents are searched too.
    /// </summary>
    public static string AppDir { get; } = FindAppDir();

    private static string FindAppDir()
    {
        var candidates = new List<string> { Environment.CurrentDirectory };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 5 && dir is not null; i++, dir = dir.Parent) candidates.Add(dir.FullName);
        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "config.json")) || Directory.Exists(Path.Combine(c, "librespot")))
                return c;
        }
        return Environment.CurrentDirectory;
    }

    public static string HomeDir
    {
        get
        {
            // A config.json in the daemon's folder wins; otherwise %APPDATA%\SpotifyRadio.
            var over = Environment.GetEnvironmentVariable("SPOTIFY_RADIO_HOME");
            var dir = !string.IsNullOrWhiteSpace(over) ? over
                : File.Exists(Path.Combine(AppDir, "config.json")) ? AppDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyRadio");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ConfigPath => Path.Combine(HomeDir, "config.json");
    public static string TokensPath => Path.Combine(HomeDir, "tokens.json");

    [System.Text.Json.Serialization.JsonIgnore]
    public string RedirectUri => $"http://127.0.0.1:{AuthPort}/callback";

    /// <summary>The game's gamedata folder, derived from ipcDir (..\gamedata).</summary>
    public string? ResolveGameDataDir()
    {
        if (string.IsNullOrWhiteSpace(IpcDir)) return null;
        var root = Directory.GetParent(IpcDir.TrimEnd('\\', '/'))?.FullName;
        return root is null ? null : Path.Combine(root, "gamedata");
    }

    public bool Wants(string source) => Sources?.Contains(source, StringComparer.OrdinalIgnoreCase) == true;

    public static Config Load()
    {
        if (!File.Exists(ConfigPath)) return new Config();
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json) ?? new Config();
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg)
    {
        lock (Gate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }
}
