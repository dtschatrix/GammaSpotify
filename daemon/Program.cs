using SpotifyRadio;

Console.Title = "Spotify Radio daemon";
using var single = new Mutex(true, "SpotifyRadioDaemon", out var first);
if (!first)
{
    Console.WriteLine("The Spotify Radio daemon is already running in another window.");
    return 1;
}
var cfg = Config.Load();

if (string.IsNullOrWhiteSpace(cfg.ClientId))
{
    Console.WriteLine("""
        First run - Spotify needs an app registration (free, takes a minute):

          1. Open https://developer.spotify.com/dashboard and create an app.
          2. Redirect URI:  %REDIRECT%
          3. API used:      Web API
          4. Copy the Client ID from the app's settings and paste it here.

        """.Replace("%REDIRECT%", cfg.RedirectUri));
    Console.Write("Client ID: ");
    cfg.ClientId = (Console.ReadLine() ?? "").Trim();
    if (cfg.ClientId.Length == 0)
    {
        Console.WriteLine("No Client ID given, exiting.");
        return 1;
    }
}
if (string.IsNullOrWhiteSpace(cfg.IpcDir))
{
    Console.WriteLine("""

        Where is the game? Paste the path of your Anomaly folder - the one that contains
        "appdata" and "gamedata" (for GAMMA that is the Anomaly folder, not the GAMMA one).

        """);
    while (true)
    {
        Console.Write("Anomaly folder: ");
        var typed = Console.ReadLine();
        if (typed is null) { Console.WriteLine("No folder given, exiting."); return 1; }
        var dir = typed.Trim().Trim('"').TrimEnd('\\', '/');
        if (dir.Length == 0) continue;
        if (!Path.GetFileName(dir).Equals("appdata", StringComparison.OrdinalIgnoreCase)) dir = Path.Combine(dir, "appdata");
        if (Directory.Exists(dir)) { cfg.IpcDir = dir; break; }
        Console.WriteLine($"  There is no folder \"{dir}\" - try again.");
    }
}
cfg.Save();
Log.Info($"Config: {Config.ConfigPath}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var auth = new SpotifyAuth(cfg, http);
var spotify = new SpotifyClient(cfg, auth, http);

try
{
    await auth.EnsureLoggedInAsync(cts.Token);
    var state = await spotify.GetStateAsync(cts.Token);
    Log.Info(state.Status switch
    {
        "nodevice" => "Spotify: no active device right now.",
        "idle" => $"Spotify: device \"{state.Device}\", nothing queued.",
        _ => $"Spotify: {state.Status} on \"{state.Device}\": {state.Artist} - {state.Title}",
    });
    var spotifySource = new SpotifySource(cfg, spotify);
    var youtube = new YouTubeSource(cfg, http);
    var server = new GameServer(cfg, spotifySource, youtube, new TextCodec(cfg.Encoding));
    var audio = new GameAudio(cfg, new IAudioSource[] { spotifySource, youtube }, server.BroadcastAsync, () => server.GameQuiet);
    server.Audio = audio;
    AppDomain.CurrentDomain.ProcessExit += (_, _) => audio.Kill(); // never leave librespot running on its own
    var audioTask = Task.Run(() => audio.RunAsync(cts.Token));
    try { await server.RunAsync(cts.Token); }
    finally { audio.Kill(); }
}
catch (OperationCanceledException) { }
catch (Exception e)
{
    Log.Info("Fatal: " + e.Message);
    return 1;
}
return 0;
