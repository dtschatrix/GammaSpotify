using SpotifyRadio;

// --install-youtube: download yt-dlp and ffmpeg if missing; --update-youtube: a new yt-dlp (YouTube breaks old ones)
if (args.Length > 0 && args[0] is "--install-youtube" or "--update-youtube")
    return await YouTubeTools.CommandAsync(update: args[0] == "--update-youtube");

Console.Title = "Spotify Radio daemon";
using var single = new Mutex(true, "SpotifyRadioDaemon", out var first);
if (!first)
{
    Console.WriteLine("The Spotify Radio daemon is already running in another window.");
    return 1;
}
var cfg = Config.Load();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (cfg.Sources is null)
{
    // first run, or a config from before YouTube: an existing Spotify setup is the default
    var hadSpotify = !string.IsNullOrWhiteSpace(cfg.ClientId);
    Console.WriteLine("""

        What should play in the game?
          1  Spotify        needs Spotify Premium
          2  YouTube Music  free; downloads two programs (yt-dlp, ffmpeg)
          3  both

        """);
    while (cfg.Sources is null)
    {
        Console.Write(hadSpotify ? "Choice [1]: " : "Choice: ");
        var typed = Console.ReadLine();
        if (typed is null)
        {
            if (!hadSpotify) { Console.WriteLine("Nothing chosen, exiting."); return 1; }
            typed = "1";
        }
        typed = typed.Trim();
        if (typed.Length == 0 && hadSpotify) typed = "1";
        cfg.Sources = typed switch
        {
            "1" => new[] { "spotify" },
            "2" => new[] { "youtube" },
            "3" => new[] { "spotify", "youtube" },
            _ => null,
        };
        if (cfg.Sources is null) Console.WriteLine("  Type 1, 2 or 3.");
    }
    cfg.Save();
}
if (!cfg.Wants("spotify") && !cfg.Wants("youtube"))
{
    Console.WriteLine($"\"sources\" in {Config.ConfigPath} names neither spotify nor youtube. Remove it to choose again.");
    return 1;
}

if (cfg.Wants("spotify") && string.IsNullOrWhiteSpace(cfg.ClientId))
{
    Console.WriteLine("""

        Spotify needs an app registration (free, takes a minute):

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
if (cfg.Wants("youtube") && !YouTubeTools.Installed && !await YouTubeTools.OfferAsync(cts.Token))
{
    if (!cfg.Wants("spotify"))
    {
        Console.WriteLine("Without yt-dlp and ffmpeg there is nothing to play. Start again to download them.");
        return 1;
    }
    Log.Info("YouTube: off for now. Start again to download yt-dlp and ffmpeg, or run the daemon with --install-youtube.");
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

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

try
{
    SpotifySource? spotifySource = null;
    if (cfg.Wants("spotify"))
    {
        var auth = new SpotifyAuth(cfg, http);
        var spotify = new SpotifyClient(cfg, auth, http);
        await auth.EnsureLoggedInAsync(cts.Token);
        var state = await spotify.GetStateAsync(cts.Token);
        Log.Info(state.Status switch
        {
            "nodevice" => "Spotify: no active device right now.",
            "idle" => $"Spotify: device \"{state.Device}\", nothing queued.",
            _ => $"Spotify: {state.Status} on \"{state.Device}\": {state.Artist} - {state.Title}",
        });
        spotifySource = new SpotifySource(cfg, spotify);
    }
    var youtube = cfg.Wants("youtube") ? new YouTubeSource(cfg, http) : null;

    // the first one that can play is what the game hears at the start: where the PDA's switch was left
    var sources = new List<IAudioSource>();
    if (spotifySource is not null) sources.Add(spotifySource);
    if (youtube is not null) sources.Insert(cfg.LastSource == "youtube" ? 0 : sources.Count, youtube);
    var server = new GameServer(cfg, spotifySource, youtube, new TextCodec(cfg.Encoding));
    var audio = new GameAudio(cfg, sources, server.BroadcastAsync, () => server.GameQuiet);
    server.Audio = audio;
    AppDomain.CurrentDomain.ProcessExit += (_, _) => audio.Kill(); // never leave librespot or ffmpeg running on their own
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
