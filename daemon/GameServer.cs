using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SpotifyRadio;

/// <summary>
/// Line protocol for the game, one command per line, tab-separated replies.
///   game -> daemon: hello | ping | state | toggle | play | pause | next | prev | vol N | volrel +-N |
///                   shuffle on|off | seek MS | search TEXT | playlists | playuri URI | queue URI | source NAME [URI MS] |
///                   upnext | unqueue INDEX URI |
///                   stream start MODE FOLDER | stream stop | gamevol 0..100 | buf MS LAST [hold] | log TEXT
///   daemon -> game: hello | pong | state ... | ok CMD | error MSG | results_begin / result / results_end |
///                   chunk NAME MS INDEX | stream started MODE VOLUME | stream stopped|failed|unavailable|ended |
///                   audio hold|resume|flush | track INDEX POS DURATION URI ARTIST TITLE |
///                   upnext_begin SOURCE / upnext_item N KIND URI TITLE SUBTITLE / upnext_end COUNT
///
/// Two transports carry the same lines:
///   - TCP on 127.0.0.1 (only usable when the game exposes LuaSocket, i.e. lua_debug 1)
///   - two text files in the game's appdata folder (works everywhere, used by default)
/// </summary>
public sealed class GameServer
{
    private interface ILink
    {
        string Name { get; }
        bool Active { get; }
        Task WriteAsync(byte[] line, CancellationToken ct);
    }

    private sealed class TcpLink : ILink
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public required int Id { get; init; }
        public string Name => $"tcp #{Id}";
        public bool Active => true;

        public async Task WriteAsync(byte[] line, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try { await Stream.WriteAsync(line, ct); }
            catch (IOException) { /* reader loop cleans up */ }
            finally { _gate.Release(); }
        }
    }

    private sealed class FileLink : ILink
    {
        private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _gate = new(1, 1);
        public required string OutPath { get; init; }
        public DateTime LastCommandUtc { get; set; } = DateTime.MinValue;
        public string Name => "file";
        public bool Active => DateTime.UtcNow - LastCommandUtc < IdleAfter;

        public async Task WriteAsync(byte[] line, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                // Opened per write so the game can always read it; retry if the game has it open at this instant.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(OutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        fs.Write(line);
                        return;
                    }
                    catch (IOException) when (attempt < 5) { await Task.Delay(20, ct); }
                }
            }
            finally { _gate.Release(); }
        }
    }

    public const string CmdFileName = "spotify_radio_cmd.txt";
    public const string OutFileName = "spotify_radio_out.txt";
    private const long MaxFileBytes = 1024 * 1024;

    private readonly Config _cfg;
    private readonly IPlayer? _spotify;
    private readonly IPlayer? _youtube;
    private readonly TextCodec _codec;
    private readonly ConcurrentDictionary<int, TcpLink> _tcpLinks = new();
    private FileLink? _fileLink;
    private bool _fileWasActive;

    /// <summary>Set by Program once both objects exist.</summary>
    public GameAudio? Audio { get; set; }

    /// <summary>What the transport buttons control: the source the game hears.</summary>
    private IPlayer Player => Audio?.Current as IPlayer ?? _spotify ?? _youtube!;

    /// <summary>Who plays or queues an address: YouTube's start with "yt:".</summary>
    private IPlayer PlayerFor(string uri) => YouTubeSource.IsUri(uri)
        ? _youtube ?? throw new SpotifyException("YouTube is not set up in this daemon (\"sources\" in config.json)")
        : _spotify ?? throw new SpotifyException("Spotify is not set up in this daemon (\"sources\" in config.json)");

    /// <summary>The PDA's source switch: "spotify" or "youtube".</summary>
    private static string IdOf(IPlayer player) => player is YouTubeSource ? "youtube" : "spotify";

    /// <summary>The sources the switch offers: set up, and able to play through the game.</summary>
    private IEnumerable<IPlayer> Switchable => new[] { _spotify, _youtube }
        .Where(p => p is IAudioSource { Available: true })
        .Select(p => p!);

    private DateTime _lastHeardUtc = DateTime.MinValue;
    private volatile List<SearchItem> _lastResults = new();   // the list the PDA shows

    /// <summary>Time since the game last said anything.</summary>
    public TimeSpan GameQuiet => DateTime.UtcNow - _lastHeardUtc;

    public async Task BroadcastAsync(string[] fields, CancellationToken ct)
    {
        foreach (var link in ActiveLinks().ToList())
            await SendAsync(link, ct, fields);
    }
    private string _lastSignature = "";
    private string _lastPollError = "";
    private int _nextId;

    /// <param name="spotify">Null when only YouTube is set up.</param>
    /// <param name="youtube">Null when only Spotify is set up.</param>
    public GameServer(Config cfg, IPlayer? spotify, IPlayer? youtube, TextCodec codec)
    {
        _cfg = cfg;
        if (spotify is null && youtube is null) throw new ArgumentException("no player");
        _spotify = spotify;
        _youtube = youtube;
        _codec = codec;
    }

    /// <summary>
    /// The usual install mistake leaves the mod's scripts out of the game: the game runs, the
    /// daemon never hears from it, and nothing says why. If Anomaly has been running for a while
    /// without a word, say what to check. Once per game run. Scripts only start when a save is
    /// loaded, so this can also mean the player is still in the main menu.
    /// </summary>
    private async Task WatchForSilentGameAsync(CancellationToken ct)
    {
        var hinted = false;
        DateTime? runningSince = null;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { return; }
            bool running;
            try
            {
                running = System.Diagnostics.Process.GetProcesses()
                    .Any(p => p.ProcessName.StartsWith("AnomalyDX", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception) { continue; }
            if (!running) { runningSince = null; hinted = false; continue; }
            runningSince ??= DateTime.UtcNow;
            var heardSinceStart = _lastHeardUtc > runningSince;
            if (hinted || heardSinceStart || DateTime.UtcNow - runningSince < TimeSpan.FromMinutes(4)) continue;
            hinted = true;
            Log.Info("The game has been running for a few minutes and the mod has not said anything.");
            Log.Info("  If you are already in game (not in the main menu), the mod is not loaded:");
            Log.Info("  - in MO2, a red X next to the mod means its gamedata folder is not at the top of the mod;");
            Log.Info("  - check that the mod is ticked in the profile you start the game with;");
            Log.Info($"  - ipcDir in config.json must be the appdata folder of this game: {_cfg.IpcDir}");
        }
    }

    private IEnumerable<ILink> ActiveLinks()
    {
        foreach (var l in _tcpLinks.Values) yield return l;
        if (_fileLink is { Active: true } f) yield return f;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(() => PollLoopAsync(ct), ct);
        _ = Task.Run(() => WatchForSilentGameAsync(ct), ct);
        if (!string.IsNullOrWhiteSpace(_cfg.IpcDir))
            _ = Task.Run(() => FileLoopAsync(_cfg.IpcDir, ct), ct);
        else
            Log.Info("ipcDir is not set in config.json - file link disabled, TCP only.");

        var listener = new TcpListener(IPAddress.Loopback, _cfg.GamePort);
        try
        {
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            Log.Info($"Waiting for the game (TCP 127.0.0.1:{_cfg.GamePort}).");
        }
        catch (SocketException e)
        {
            // The file link does not need the port, so this is not fatal.
            Log.Info($"TCP port {_cfg.GamePort} is unavailable ({e.Message}); file link only.");
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                tcp.NoDelay = true;
                var link = new TcpLink { Tcp = tcp, Stream = tcp.GetStream(), Id = Interlocked.Increment(ref _nextId) };
                _tcpLinks[link.Id] = link;
                Log.Info($"Game connected ({link.Name}).");
                _ = Task.Run(() => ServeTcpAsync(link, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task ServeTcpAsync(TcpLink link, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(link.Stream, _codec.Incoming, false, 4096, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(ct);
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                await HandleAsync(link, line, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception e) { Log.Info($"{link.Name} error: {e.Message}"); }
        finally
        {
            _tcpLinks.TryRemove(link.Id, out _);
            link.Tcp.Dispose();
            Log.Info($"Game disconnected ({link.Name}).");
        }
    }

    /// <summary>Tails the command file the game appends to; replies are appended to the out file.</summary>
    private async Task FileLoopAsync(string dir, CancellationToken ct)
    {
        string cmdPath, outPath;
        try
        {
            Directory.CreateDirectory(dir);
            cmdPath = Path.Combine(dir, CmdFileName);
            outPath = Path.Combine(dir, OutFileName);
            // Old commands are never replayed; the files only get reset when they have grown large.
            if (!File.Exists(cmdPath) || new FileInfo(cmdPath).Length > MaxFileBytes) File.WriteAllBytes(cmdPath, Array.Empty<byte>());
            if (!File.Exists(outPath) || new FileInfo(outPath).Length > MaxFileBytes) File.WriteAllBytes(outPath, Array.Empty<byte>());
        }
        catch (Exception e)
        {
            Log.Info($"File link disabled, cannot use \"{dir}\": {e.Message}");
            return;
        }

        var link = new FileLink { OutPath = outPath };
        _fileLink = link;
        _ = Task.Run(() => WatchCmdFileAsync(cmdPath, ct), CancellationToken.None);
        var offset = new FileInfo(cmdPath).Length;
        var pending = new List<byte>();
        Log.Info($"Waiting for the game (files in {dir}).");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, ct);
                if (_fileWasActive && !link.Active)
                {
                    _fileWasActive = false;
                    Log.Info("Game went quiet (file link).");
                }

                var info = new FileInfo(cmdPath);
                if (!info.Exists) { offset = 0; continue; }
                if (info.Length < offset) { offset = 0; pending.Clear(); }
                if (info.Length == offset) continue;

                byte[] chunk;
                using (var fs = new FileStream(cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    chunk = new byte[fs.Length - offset];
                    var read = fs.Read(chunk, 0, chunk.Length);
                    if (read < chunk.Length) Array.Resize(ref chunk, read);
                    offset += read;
                }
                pending.AddRange(chunk);

                int nl;
                while ((nl = pending.IndexOf((byte)'\n')) >= 0)
                {
                    var line = _codec.Incoming.GetString(pending.GetRange(0, nl).ToArray()).Trim();
                    pending.RemoveRange(0, nl + 1);
                    if (line.Length == 0) continue;
                    link.LastCommandUtc = DateTime.UtcNow;
                    if (!_fileWasActive)
                    {
                        _fileWasActive = true;
                        Log.Info("Game connected (file link).");
                    }
                    await HandleAsync(link, line, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* file busy for a moment; try again next tick */ }
            catch (Exception e) { Log.Info("File link error: " + e.Message); }
        }
    }

    /// <summary>
    /// Commands are handled one at a time and a Spotify call can take seconds, so whether the
    /// game is still writing is watched separately.
    /// </summary>
    private async Task WatchCmdFileAsync(string cmdPath, CancellationToken ct)
    {
        long seen = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);
                var info = new FileInfo(cmdPath);
                var length = info.Exists ? info.Length : 0;
                if (seen >= 0 && length != seen) _lastHeardUtc = DateTime.UtcNow;
                seen = length;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }

    private async Task HandleAsync(ILink link, string line, CancellationToken ct)
    {
        var space = line.IndexOf(' ');
        var cmd = (space < 0 ? line : line[..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : line[(space + 1)..].Trim();
        _lastHeardUtc = DateTime.UtcNow;
        if (cmd == "buf")
        {
            // how much audio the game holds, about once a second; also shows that it is running
            // buf <ms held> <index of the last chunk received> [hold]
            var b = arg.Split(' ');
            if (b.Length >= 2 && int.TryParse(b[0], out var ms) && int.TryParse(b[1], out var last))
                Audio?.OnGameBuffer(ms, last, b.Length > 2 && b[2] == "hold");
            return;
        }
        if (cmd == "ping")
        {
            await SendAsync(link, ct, "pong");
            return;
        }
        if (cmd == "gamevol")
        {
            // volume of the in-game sound; the game applies it itself, this only remembers it
            if (int.TryParse(arg, out var v))
            {
                _cfg.GameVolume = Math.Clamp(v, 0, 100);
                try { _cfg.Save(); } catch (IOException) { /* keep it for this run */ }
            }
            return;
        }
        if (cmd == "upnext")
        {
            // asked every few seconds while the PDA tab is open, so not logged
            try { await SendUpNextAsync(link, ct); }
            catch (Exception e) when (e is SpotifyException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            return;
        }
        if (cmd == "log")
        {
            Log.Info("   game: " + arg);
            return;
        }
        if (cmd == "stream")
        {
            // stream start <world|head> <folder>  |  stream stop
            Log.Info($"<- {line}");
            try
            {
                var parts = arg.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && parts[0] == "start" && Audio is not { Available: true })
                {
                    // no audio source (librespot is missing): the game stays a remote control, without an error message
                    await SendAsync(link, ct, "stream", "unavailable");
                }
                else if (parts.Length == 3 && parts[0] == "start")
                {
                    await Audio!.StartStreamAsync(parts[1], parts[2], ct);
                    await SendAsync(link, ct, "stream", "started", parts[1], _cfg.GameVolume.ToString());
                }
                else if (Audio is not null)
                {
                    await Audio.StopStreamAsync(ct);
                    await SendAsync(link, ct, "stream", "stopped");
                }
                _ = RefreshSoonAsync(ct);
            }
            catch (Exception e) when (e is SpotifyException or InvalidOperationException or HttpRequestException or IOException)
            {
                Log.Info($"   error: {e.Message}");
                await SendAsync(link, ct, "stream", "failed");
                await SendAsync(link, ct, "error", e.Message);
            }
            return;
        }
        Log.Info($"<- {line}");
        try
        {
            switch (cmd)
            {
                case "hello":
                    await SendAsync(link, ct, "hello", "SpotifyRadio", "1");
                    await SendStateAsync(link, await Player.GetStateAsync(ct), ct);
                    return;
                case "state":
                    await SendStateAsync(link, await Player.GetStateAsync(ct), ct);
                    return;
                case "toggle":
                    if ((await Player.GetStateAsync(ct)).Status == "playing") await Player.PauseAsync(ct);
                    else await Player.PlayAsync(ct);
                    break;
                case "play": await Player.PlayAsync(ct); break;
                case "pause": await Player.PauseAsync(ct); break;
                case "next": await Player.NextAsync(ct); break;
                case "prev": await Player.PreviousAsync(ct); break;
                case "vol":
                    await Player.SetVolumeAsync(ParseInt(arg), ct);
                    break;
                case "volrel":
                {
                    var current = (await Player.GetStateAsync(ct)).Volume;
                    if (current < 0) throw new SpotifyException("No active Spotify device to set volume on");
                    await Player.SetVolumeAsync(current + ParseInt(arg), ct);
                    break;
                }
                case "shuffle":
                    await Player.SetShuffleAsync(arg is "on" or "1" or "true", ct);
                    break;
                case "seek":
                    await Player.SeekAsync(Math.Max(0, ParseInt(arg)), ct);
                    break;
                case "playuri":
                    // A Spotify track from the list plays with the list's tracks after it, so next goes on down the list
                    if (_spotify is SpotifySource spotify && TracksFrom(arg) is { Count: > 1 } tracks)
                        await spotify.PlayTracksAsync(tracks, ct);
                    else
                        await PlayerFor(arg).PlayUriAsync(arg, ct);
                    break;
                case "queue":
                    await PlayerFor(arg).QueueAsync(arg, ct);
                    break;
                case "source":
                    await SwitchAsync(arg, ct);
                    await SendStateAsync(link, await Player.GetStateAsync(ct), ct);
                    break;
                case "search" when arg.StartsWith("yt ", StringComparison.OrdinalIgnoreCase):
                    // "yt ..." searches YouTube Music whatever the switch is on
                    await SendResultsAsync(link, arg, await PlayerFor(YouTubeSource.VideoPrefix).SearchAsync(arg[3..].Trim(), ct), ct);
                    return;
                case "search":
                    // the source the switch is on, which is the one the game hears
                    if (arg.Length == 0) throw new SpotifyException("Empty search");
                    await SendResultsAsync(link, arg, await Player.SearchAsync(arg, ct), ct);
                    return;
                case "playlists":
                    await SendResultsAsync(link, "playlists", await Player.MyPlaylistsAsync(ct), ct);
                    return;
                case "unqueue":
                {
                    // unqueue <index in the up-next list> <its address>: the address guards against a list that moved on
                    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) throw new FormatException("unqueue needs a position and an address");
                    await Player.RemoveUpNextAsync(ParseInt(parts[0]), parts[1], ct);
                    await SendUpNextAsync(link, ct);
                    return;
                }
                default:
                    throw new SpotifyException("Unknown command: " + cmd);
            }
            RememberSource();                        // playing something of the other source switches too
            await SendAsync(link, ct, "ok", cmd);
            _ = RefreshSoonAsync(ct);
        }
        catch (Exception e) when (e is SpotifyException or InvalidOperationException or HttpRequestException or FormatException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var msg = e is TaskCanceledException ? "Spotify did not answer in time" : e.Message;
            Log.Info($"   error: {msg}");
            await SendAsync(link, ct, "error", msg);
        }
    }

    /// <summary>
    /// "source spotify|youtube [URI MS]": the PDA's switch. URI and MS say what the game was playing
    /// on the source it leaves, and where, so that switching back carries on from there.
    /// </summary>
    private async Task SwitchAsync(string arg, CancellationToken ct)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var target = id switch
        {
            "spotify" => _spotify ?? throw new SpotifyException("Spotify is not set up in this daemon (\"sources\" in config.json)"),
            "youtube" => _youtube ?? throw new SpotifyException("YouTube is not set up in this daemon (\"sources\" in config.json)"),
            _ => throw new SpotifyException("Unknown source: " + id),
        };
        if (Audio is not { } audio || target is not IAudioSource source || audio.IsCurrent(source)) return;
        if (!source.Available) throw new SpotifyException(source.Missing);
        if (parts.Length == 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            audio.Current.NoteHeard(parts[1], ms);
        Log.Info($"Game audio: switched to {source.Name} in the PDA.");
        await source.SwitchInAsync(ct);
    }

    /// <summary>The source the game hears is kept in the config, so the switch stays where it was left.</summary>
    private void RememberSource()
    {
        if (Audio?.Current is not IPlayer p || _cfg.LastSource == IdOf(p)) return;
        _cfg.LastSource = IdOf(p);
        try { _cfg.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kept for this run */ }
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, out var n) ? n : throw new FormatException("Expected a number, got: " + s);

    private const int UpNextMax = 11;            // rows in the PDA's up-next column

    /// <summary>What plays next on the source the game hears, as one write.</summary>
    private async Task SendUpNextAsync(ILink link, CancellationToken ct)
    {
        var player = Player;
        var items = await player.UpNextAsync(UpNextMax, ct);
        var sb = new StringBuilder();
        sb.Append(Format("upnext_begin", IdOf(player)));
        for (var i = 0; i < items.Count; i++)
            sb.Append(Format("upnext_item", i.ToString(), items[i].Kind, items[i].Uri, items[i].Title, items[i].Subtitle));
        sb.Append(Format("upnext_end", items.Count.ToString()));
        await link.WriteAsync(_codec.Wire.GetBytes(sb.ToString()), ct);
    }

    /// <summary>The Spotify tracks of the list the PDA shows, from this one on; null when it is not in it.</summary>
    private List<string>? TracksFrom(string uri)
    {
        if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        var list = _lastResults;
        var at = list.FindIndex(r => r.Uri == uri);
        return at < 0 ? null : list.Skip(at).Where(r => r.Uri.StartsWith("spotify:track:", StringComparison.Ordinal)).Take(50).Select(r => r.Uri).ToList();
    }

    private async Task SendResultsAsync(ILink link, string title, List<SearchItem> items, CancellationToken ct)
    {
        _lastResults = items;
        // One write for the whole listing so the game never sees half of it.
        var sb = new StringBuilder();
        sb.Append(Format("results_begin", title));
        for (var i = 0; i < items.Count; i++)
            sb.Append(Format("result", (i + 1).ToString(), items[i].Kind, items[i].Uri, items[i].Title, items[i].Subtitle));
        sb.Append(Format("results_end", items.Count.ToString()));
        await link.WriteAsync(_codec.Wire.GetBytes(sb.ToString()), ct);
    }

    private Task SendStateAsync(ILink link, PlayerState s, CancellationToken ct) =>
        SendAsync(link, ct, "state", s.Status, s.Artist, s.Title, s.ProgressMs.ToString(), s.DurationMs.ToString(),
            s.Volume.ToString(), s.Device, s.Shuffle ? "1" : "0", s.Uri,
            IdOf(Player), string.Join(",", Switchable.Select(IdOf)));   // where the switch is, and what it offers

    private string Format(params string[] fields) => string.Join('\t', fields.Select(_codec.Clean)) + "\n";

    private Task SendAsync(ILink link, CancellationToken ct, params string[] fields) =>
        link.WriteAsync(_codec.Wire.GetBytes(Format(fields)), ct);

    /// <summary>Spotify takes a moment to apply a command; push the new state shortly after.</summary>
    private async Task RefreshSoonAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(600, ct);
            await PollOnceAsync(force: true, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Info("Refresh failed: " + e.Message); }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.PollSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                if (!ActiveLinks().Any()) continue; // don't hit the API while the game isn't there
                await PollOnceAsync(force: false, ct);
                _lastPollError = "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                if (e.Message != _lastPollError) Log.Info("Poll failed: " + e.Message);
                _lastPollError = e.Message;
            }
        }
    }

    private async Task PollOnceAsync(bool force, CancellationToken ct)
    {
        var state = await Player.PollAsync(ct);
        var signature = IdOf(Player) + state.Signature;
        if (!force && signature == _lastSignature) return;
        _lastSignature = signature;
        foreach (var link in ActiveLinks().ToList())
            await SendStateAsync(link, state, ct);
    }
}
