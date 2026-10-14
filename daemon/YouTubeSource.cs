using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpotifyRadio;

/// <summary>
/// YouTube Music as the audio source, and as the player the PDA controls while it plays.
///
/// yt-dlp finds a track's audio stream and ffmpeg decodes it to the PCM GameAudio takes. Nothing
/// outside runs the queue, so this class is the player: the list, next and previous, shuffle,
/// pause (by not reading ffmpeg's output) and, when the list runs out, a radio of the last track.
/// It decodes the audio itself, so it knows the exact byte where every track starts and the
/// track marks need no guessing.
///
/// Needs yt\yt-dlp.exe (or the unpacked release in yt\yt-dlp_win\, which starts faster) and
/// yt\ffmpeg.exe next to run.bat. YouTube limits how often one address may ask for streams, so
/// only the track that plays and the one after it are looked up, and a "not a bot" answer stops
/// all lookups for a while instead of retrying.
/// </summary>
public sealed class YouTubeSource : IAudioSource, IPlayer
{
    public const string VideoPrefix = "yt:video:";
    public const string PlaylistPrefix = "yt:playlist:";
    public const string AlbumPrefix = "yt:album:";
    private const double BytesPerMs = 44100 * 4 / 1000.0;
    private const int MaxRestarts = 3;       // a stream that breaks off is opened again this often
    private const int ListMax = 100;         // tracks taken from a playlist or album
    private const int RadioMax = 25;         // tracks taken from a radio at a time
    private const int BoundaryLog = 32;
    private static readonly TimeSpan BlockedFor = TimeSpan.FromMinutes(10);

    public static bool IsUri(string uri) => uri.StartsWith("yt:", StringComparison.Ordinal);

    private sealed record Item(string Id, string Title, string Artist, int DurationMs);
    private sealed record Resolved(Item Item, string Url, DateTime ValidUntilUtc);
    private sealed record Boundary(long OutByte, string Id, double PosMs); // from this byte of our output on, it is this track at PosMs

    private sealed class Track
    {
        public required Item Item { get; init; }
        public required string Url { get; set; }
        public required Process Process { get; set; }
        public required Stream Pipe { get; set; }
        public double StartMs { get; set; }  // where this ffmpeg started in the track
        public long Bytes { get; set; }      // PCM this ffmpeg has given
        public int Restarts { get; set; }
        public double PositionMs => StartMs + Bytes / BytesPerMs;
    }

    private readonly Config _cfg;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private GameAudio? _audio;
    private CancellationToken _life;

    // the list (guarded by _sync)
    private readonly List<Item> _list = new();
    private int _pos = -1;
    private int _insertAt;                   // where the next queued track goes
    private readonly List<Item> _queuedEarly = new(); // queued before anything was played
    private bool _shuffle;
    private readonly HashSet<string> _played = new();

    // playback (guarded by _sync)
    private Track? _track;                   // being read
    private Item? _shown;                    // what the PDA shows: playing, or being opened
    private bool _paused;
    private int _gen;                        // bumped by every change the player asks for
    private long _outBytes;                  // PCM handed to the streamer since the pump started
    private readonly List<Boundary> _boundaries = new();
    private TaskCompletionSource _wake = NewWake();

    // lookups (guarded by _sync)
    private readonly Dictionary<string, Item> _known = new();
    private readonly Dictionary<string, Resolved> _resolved = new();
    private DateTime _blockedUntilUtc;
    private Task? _prefetch;

    public YouTubeSource(Config cfg, HttpClient http)
    {
        _cfg = cfg;
        _http = http;
    }

    // ------------------------------------------------------------------ tools

    private static string? YtDlp => YouTubeTools.YtDlp;
    private static string? Ffmpeg => YouTubeTools.Ffmpeg;

    public string Name => "YouTube";
    public bool Available => YouTubeTools.Installed;
    public string Missing => "yt-dlp and ffmpeg are not in the yt folder: start the daemon with --install-youtube";

    private static SpotifyException NotSetUp() => new("YouTube needs yt-dlp and ffmpeg: start the daemon with --install-youtube");

    public void Attach(GameAudio audio) => _audio = audio;

    private GameAudio Audio => _audio ?? throw new InvalidOperationException("YouTube is not attached to the game audio");
    private bool IsCurrent => _audio is { } a && a.IsCurrent(this);

    // ------------------------------------------------------------------ the pump

    public async Task RunAsync(CancellationToken ct)
    {
        _life = ct;
        while (!ct.IsCancellationRequested)
        {
            lock (_sync)
            {
                // the pump counts its bytes from zero, and so does this
                _outBytes = 0;
                _boundaries.Clear();
                if (_track is { } t) _boundaries.Add(new Boundary(0, t.Item.Id, t.PositionMs));
            }
            try
            {
                await Audio.PumpAsync(this, ReadAsync, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Info("YouTube: " + e.Message);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        Kill();
    }

    public void Kill()
    {
        Track? t;
        lock (_sync) t = _track;
        KillProcess(t?.Process);
    }

    /// <summary>The pump's read: PCM of the track being played. Waits while paused or while nothing plays.</summary>
    private async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            Track? track;
            Stream? pipe;
            Task wake;
            lock (_sync)
            {
                track = _paused ? null : _track;
                pipe = track?.Pipe;
                wake = _wake.Task;
            }
            if (track is null || pipe is null)
            {
                await wake.WaitAsync(ct);
                continue;
            }

            int n;
            try { n = await ReadFramesAsync(pipe, buffer, ct); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { n = 0; }

            lock (_sync)
            {
                // Changed while reading: what came is the old audio and is not handed out.
                if (!ReferenceEquals(_track, track) || !ReferenceEquals(track.Pipe, pipe)) continue;
                if (n > 0)
                {
                    track.Bytes += n;
                    _outBytes += n;
                    return n;
                }
            }
            await EndedAsync(track, ct);
        }
    }

    private static async Task<int> ReadFramesAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var n = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer[n..], ct);
            if (read == 0) return n - n % 4;
            n += read;
            if (n % 4 == 0) return n;            // whole stereo frames only
        }
    }

    /// <summary>ffmpeg's output ended: the track is over, or the stream broke off.</summary>
    private async Task EndedAsync(Track track, CancellationToken ct)
    {
        var at = track.PositionMs;
        var length = track.Item.DurationMs;
        var over = length > 0 ? at >= length - 5000 : track.Bytes > 0;
        if (!over && track.Restarts < MaxRestarts)
        {
            // Nothing came at all: the address was refused (seen in game: 403 on a fresh one).
            // Otherwise the connection dropped, e.g. after a long pause: go on from where it stopped.
            var refused = track.Bytes == 0;
            Log.Info(refused
                ? $"YouTube: no audio from that address, asking for a new one ({track.Restarts + 1}/{MaxRestarts})."
                : $"YouTube: audio stopped at {at / 1000:0} s of {length / 1000} s, starting again from there ({track.Restarts + 1}/{MaxRestarts}).");
            try
            {
                var url = track.Url;
                if (refused || track.Restarts > 0) url = (await ResolveAsync(track.Item.Id, fresh: true, ct)).Url;
                var process = StartFfmpeg(url, at);
                lock (_sync)
                {
                    if (ReferenceEquals(_track, track))
                    {
                        (process, track.Process) = (track.Process, process); // the old one is killed below
                        track.Url = url;
                        track.Pipe = track.Process.StandardOutput.BaseStream;
                        track.StartMs = at;
                        track.Bytes = 0;
                        track.Restarts++;
                    }
                }
                KillProcess(process);
                return;
            }
            catch (SpotifyException e) { Log.Info(e.Message); }
        }
        await AdvanceAsync(track, ct);
    }

    /// <summary>
    /// The track is over: the next one starts without a break. The game still holds several
    /// seconds of audio, which covers looking up the next stream if that was not done already.
    /// </summary>
    private async Task AdvanceAsync(Track ended, CancellationToken ct)
    {
        KillProcess(ended.Process);
        for (var tries = 0; tries < 5; tries++)
        {
            var next = await NextItemAsync(ended, ct);
            if (next is null) break;
            try
            {
                var r = await ResolveAsync(next.Id, fresh: false, ct);
                var process = StartFfmpeg(r.Url, 0);
                long at;
                lock (_sync)
                {
                    if (!ReferenceEquals(_track, ended))
                    {
                        KillProcess(process);            // the player asked for something else meanwhile
                        return;
                    }
                    _track = NewTrack(r, process, 0);
                    _shown = r.Item;
                    NotePlayed(r.Item.Id);
                    at = _outBytes;
                    AddBoundary(at, r.Item.Id, 0);
                }
                Log.Info($"YouTube: next: {r.Item.Artist} - {r.Item.Title}");
                await Audio.MarkAtSourceByteAsync(this, at, 0, StateOf(r.Item, 0, "playing"), ct);
                Prefetch();
                return;
            }
            catch (SpotifyException e)
            {
                Log.Info(e.Message);
                await TellAsync(e.Message, ct);
                if (Blocked) break;
            }
        }
        lock (_sync)
        {
            if (!ReferenceEquals(_track, ended)) return;
            _track = null;
            _shown = null;
        }
        Log.Info("YouTube: nothing more to play.");
    }

    /// <summary>Moves on in the list. At its end a radio of the track that just ended is added.</summary>
    private async Task<Item?> NextItemAsync(Track ended, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_track, ended)) return null;
            if (_pos + 1 < _list.Count) return MoveTo(_pos + 1);
        }
        if (!await AddRadioAsync(ended.Item.Id, ct)) return null;
        lock (_sync)
        {
            if (!ReferenceEquals(_track, ended) || _pos + 1 >= _list.Count) return null;
            return MoveTo(_pos + 1);
        }
    }

    /// <summary>
    /// The list has run out: YouTube's mix of the given track (what YouTube Music plays as its
    /// radio) is added, without anything played already. False when nothing could be added.
    /// </summary>
    private async Task<bool> AddRadioAsync(string seedId, CancellationToken ct)
    {
        List<Item> found;
        try { found = await ListAsync($"https://www.youtube.com/watch?v={seedId}&list=RD{seedId}", RadioMax, ct); }
        catch (SpotifyException e)
        {
            Log.Info(e.Message);
            await TellAsync(e.Message, ct);
            return false;
        }
        lock (_sync)
        {
            if (_pos + 1 < _list.Count) return true; // something was added meanwhile
            var seen = new HashSet<string>(_played);
            var add = found.Where(i => i.DurationMs > 0 && seen.Add(i.Id)).ToList();
            if (add.Count == 0) return false;
            if (_shuffle) Shuffle(add, 0);
            _list.AddRange(add);
            Log.Info($"YouTube: the list is over, a radio of \"{Meta(seedId).Title}\" adds {add.Count} tracks.");
            return true;
        }
    }

    // ------------------------------------------------------------------ changes the player asks for

    /// <summary>
    /// Plays the track at _pos from <paramref name="startMs"/>: what the game holds is dropped and
    /// YouTube becomes what the game hears. Returns at once; opening the stream takes a second or
    /// two and happens in the background. Called with _changeGate held.
    /// </summary>
    private async Task StartAtPosAsync(double startMs, CancellationToken ct)
    {
        Item? item;
        Track? old;
        long cut;
        int gen;
        lock (_sync)
        {
            gen = ++_gen;
            old = _track;
            _track = null;
            item = _pos >= 0 && _pos < _list.Count ? _list[_pos] : null;
            _shown = item;
            _paused = false;
            cut = _outBytes;                     // everything handed out before this is the old track
            if (item is not null) AddBoundary(cut, item.Id, startMs);
            Wake();
        }
        KillProcess(old?.Process);
        await Audio.StartOverAsync(this, "new track", cut, ct);
        if (item is not null) _ = Task.Run(() => OpenAsync(item, startMs, gen), CancellationToken.None);
    }

    /// <summary>Opens the stream of a track the player asked for. A track that cannot be played is skipped.</summary>
    private async Task OpenAsync(Item item, double startMs, int gen)
    {
        var ct = _life;
        for (var tries = 0; tries < 5; tries++)
        {
            try
            {
                var r = await ResolveAsync(item.Id, fresh: false, ct);
                var process = StartFfmpeg(r.Url, startMs);
                lock (_sync)
                {
                    if (gen != _gen)
                    {
                        KillProcess(process);
                        return;
                    }
                    _track = NewTrack(r, process, startMs);
                    _shown = r.Item;
                    NotePlayed(r.Item.Id);
                    Wake();
                }
                Log.Info($"YouTube: {r.Item.Artist} - {r.Item.Title}");
                Prefetch();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (SpotifyException e)
            {
                Log.Info(e.Message);
                await TellAsync(e.Message, ct);
                lock (_sync)
                {
                    if (gen != _gen) return;
                    if (Blocked || _pos + 1 >= _list.Count)
                    {
                        _shown = null;
                        return;
                    }
                    item = MoveTo(_pos + 1);
                    _shown = item;
                    startMs = 0;
                    AddBoundary(_outBytes, item.Id, 0);
                }
            }
        }
    }

    public async Task PlayUriAsync(string uri, CancellationToken ct)
    {
        EnsurePlayable();
        List<Item> items;
        if (uri.StartsWith(VideoPrefix, StringComparison.Ordinal))
        {
            lock (_sync) items = new List<Item> { Known(uri[VideoPrefix.Length..]) };
        }
        else if (uri.StartsWith(PlaylistPrefix, StringComparison.Ordinal))
            items = await ListAsync("https://www.youtube.com/playlist?list=" + uri[PlaylistPrefix.Length..], ListMax, ct);
        else if (uri.StartsWith(AlbumPrefix, StringComparison.Ordinal))
            items = await ListAsync("https://music.youtube.com/browse/" + uri[AlbumPrefix.Length..], ListMax, ct);
        else
            throw new SpotifyException("Not a YouTube address: " + uri);
        items = items.Where(i => i.DurationMs > 0 || uri.StartsWith(VideoPrefix, StringComparison.Ordinal)).ToList();
        if (items.Count == 0) throw new SpotifyException("YouTube: nothing playable in that list");

        await _changeGate.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                _list.Clear();
                _list.AddRange(items);
                if (_shuffle) Shuffle(_list, 0);
                MoveTo(0);
                _list.InsertRange(1, _queuedEarly);
                _insertAt += _queuedEarly.Count;
                _queuedEarly.Clear();
            }
            await StartAtPosAsync(0, ct);
        }
        finally { _changeGate.Release(); }
    }

    public async Task NextAsync(CancellationToken ct)
    {
        await _changeGate.WaitAsync(ct);
        try
        {
            string seed;
            lock (_sync)
            {
                if (_pos < 0 || _list.Count == 0) return;
                seed = _list[_pos].Id;
                if (_pos + 1 < _list.Count) seed = "";
            }
            if (seed.Length > 0 && !await AddRadioAsync(seed, ct)) throw new SpotifyException("YouTube: nothing to play after this one");
            lock (_sync) MoveTo(Math.Min(_pos + 1, _list.Count - 1));
            await StartAtPosAsync(0, ct);
        }
        finally { _changeGate.Release(); }
    }

    /// <summary>Back to the start of the track, or to the one before when this one has only just begun.</summary>
    public async Task PreviousAsync(CancellationToken ct)
    {
        await _changeGate.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                if (_pos < 0 || _list.Count == 0) return;
                // what is heard is behind what was read by about the lead
                var heard = _track is { } t ? t.PositionMs - _cfg.LeadMs : 0;
                if (heard < 4000 && _pos > 0) MoveTo(_pos - 1);
            }
            await StartAtPosAsync(0, ct);
        }
        finally { _changeGate.Release(); }
    }

    public async Task PlayAsync(CancellationToken ct)
    {
        bool was;
        lock (_sync)
        {
            was = _paused;
            _paused = false;
            Wake();
        }
        if (was && _audio is { Held: true } a)
        {
            a.Held = false;
            await a.SendResumeAsync(ct);
        }
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        bool was;
        lock (_sync)
        {
            was = !_paused && _shown is not null;
            _paused = true;
        }
        if (was && _audio is { Streaming: true } a && a.IsCurrent(this))
        {
            a.Held = true;
            await a.SendHoldAsync(ct);
        }
    }

    public Task SetShuffleAsync(bool on, CancellationToken ct)
    {
        lock (_sync)
        {
            _shuffle = on;
            if (on && _pos + 2 < _list.Count) Shuffle(_list, _pos + 1);
            _insertAt = _pos + 1;
        }
        Prefetch();
        return Task.CompletedTask;
    }

    /// <summary>Queued tracks play after the current one, in the order they were queued.</summary>
    public Task QueueAsync(string uri, CancellationToken ct)
    {
        if (!uri.StartsWith(VideoPrefix, StringComparison.Ordinal)) throw new SpotifyException("YouTube: only single tracks can be queued");
        if (!Available) throw NotSetUp();
        var next = false;
        Item item;
        lock (_sync)
        {
            item = Known(uri[VideoPrefix.Length..]);
            if (!IsCurrent || _pos < 0 || _shown is null)
            {
                _queuedEarly.Add(item);          // plays after the next YouTube track that is started
            }
            else
            {
                var at = Math.Clamp(Math.Max(_insertAt, _pos + 1), 0, _list.Count);
                _list.Insert(at, item);
                _insertAt = at + 1;
                next = at == _pos + 1;
            }
        }
        Log.Info($"YouTube: queued {item.Title}.");
        if (next) Prefetch();
        return Task.CompletedTask;
    }

    /// <summary>The PDA's volume buttons set the game sound's volume; there is nothing to set here.</summary>
    public Task SetVolumeAsync(int percent, CancellationToken ct) => Task.CompletedTask;

    public Task<PlayerState> GetStateAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_shown is not { } shown)
                return Task.FromResult(new PlayerState("idle", "", "", "", 0, 0, _cfg.GameVolume, Name, _shuffle));
            var item = Meta(shown.Id);
            var pos = _track is { } t && t.Item.Id == item.Id ? t.PositionMs : 0;
            return Task.FromResult(StateOf(item, pos, _paused ? "paused" : "playing"));
        }
    }

    public Task<PlayerState> PollAsync(CancellationToken ct) => GetStateAsync(ct);

    /// <summary>YouTube playlists of the player's own: not yet (they would need a YouTube login).</summary>
    public Task<List<SearchItem>> MyPlaylistsAsync(CancellationToken ct) => Task.FromResult(new List<SearchItem>());

    private void EnsurePlayable()
    {
        if (!Available) throw NotSetUp();
        if (_audio is not { Streaming: true }) throw new SpotifyException("YouTube plays only through the game, and the game has not asked for the music yet");
    }

    // ------------------------------------------------------------------ as the game's audio source

    public void EnsureReady()
    {
        if (!Available) throw NotSetUp();
    }

    public Task<object?> PrepareAsync(CancellationToken ct) => Task.FromResult<object?>(null);

    public void OnStreamReset(object? prepared) { }

    public Task TakeOverAsync(object? prepared, bool resume, CancellationToken ct)
    {
        if (resume) Unpause("the game is back, playing on");
        return Task.CompletedTask;
    }

    /// <summary>The game no longer wants the stream: nobody would hear it.</summary>
    public Task ReleaseAsync(CancellationToken ct)
    {
        lock (_sync) _paused = true;
        return Task.CompletedTask;
    }

    /// <summary>Spotify plays now: stop, and forget what was playing.</summary>
    public Task DeactivateAsync(CancellationToken ct)
    {
        Track? old;
        lock (_sync)
        {
            _gen++;
            old = _track;
            _track = null;
            _shown = null;
            _paused = false;
            Wake();
        }
        KillProcess(old?.Process);
        Log.Info("YouTube: stopped.");
        return Task.CompletedTask;
    }

    public void OnNewSegment() { }

    /// <summary>The audio is decoded here, so which track the first byte of a segment is, and where, is known exactly.</summary>
    public async Task OnSegmentStartedAsync(int index, CancellationToken ct)
    {
        var at = Audio.SegmentStartSourceByte;
        Boundary? from = null;
        Item item;
        lock (_sync)
        {
            for (var i = _boundaries.Count - 1; i >= 0 && from is null; i--)
                if (_boundaries[i].OutByte <= at) from = _boundaries[i];
            if (from is null) return;
            item = Meta(from.Id);
        }
        var pos = from.PosMs + (at - from.OutByte) / BytesPerMs;
        await Audio.MarkAsync(index, pos, StateOf(item, pos, "playing"), ct);
    }

    // Pauses and changes are made here, never guessed from the audio stopping.
    public Task OnStallAsync(CancellationToken ct) => Task.CompletedTask;
    public Task OnAudioBackAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> PauseForAwayAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_paused || _shown is null) return Task.FromResult(false);
            _paused = true;
        }
        Log.Info("YouTube: the game is not answering, pausing.");
        return Task.FromResult(true);
    }

    public Task ResumeAfterAwayAsync(CancellationToken ct)
    {
        Unpause("the game is back, playing on");
        return Task.CompletedTask;
    }

    private void Unpause(string why)
    {
        lock (_sync)
        {
            if (!_paused) return;
            _paused = false;
            Wake();
        }
        Log.Info("YouTube: " + why + ".");
    }

    // ------------------------------------------------------------------ list helpers (under _sync)

    private Item MoveTo(int pos)
    {
        _pos = pos;
        _insertAt = pos + 1;
        return _list[pos];
    }

    private static void Shuffle(List<Item> list, int from)
    {
        for (var i = list.Count - 1; i > from; i--)
        {
            var j = Random.Shared.Next(from, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void NotePlayed(string id)
    {
        if (_played.Count > 1000) _played.Clear();
        _played.Add(id);
    }

    private void AddBoundary(long at, string id, double posMs)
    {
        _boundaries.Add(new Boundary(at, id, posMs));
        if (_boundaries.Count > BoundaryLog) _boundaries.RemoveAt(0);
    }

    private void Wake()
    {
        var old = _wake;
        _wake = NewWake();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Track NewTrack(Resolved r, Process process, double startMs) => new()
    {
        Item = r.Item,
        Url = r.Url,
        Process = process,
        Pipe = process.StandardOutput.BaseStream,
        StartMs = startMs,
    };

    private Item Meta(string id) => _known.TryGetValue(id, out var item) ? item : new Item(id, id, "", 0);

    private Item Known(string id) => Meta(id);

    /// <summary>Keeps what is known about a track; a listing may know less than a search did.</summary>
    private void Remember(Item item)
    {
        if (_known.TryGetValue(item.Id, out var had))
        {
            item = new Item(item.Id,
                item.Title.Length > 0 && item.Title != item.Id ? item.Title : had.Title,
                item.Artist.Length > 0 ? item.Artist : had.Artist,
                item.DurationMs > 0 ? item.DurationMs : had.DurationMs);
        }
        else if (_known.Count > 5000) _known.Clear();
        _known[item.Id] = item;
    }

    private PlayerState StateOf(Item item, double pos, string status) =>
        new(status, item.Artist, item.Title, VideoPrefix + item.Id, (int)pos, item.DurationMs, _cfg.GameVolume, Name, _shuffle);

    private async Task TellAsync(string message, CancellationToken ct)
    {
        try { await Audio.SendErrorAsync(message, ct); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ looking things up

    private bool Blocked => DateTime.UtcNow < _blockedUntilUtc;

    private const string BlockedMessage = "YouTube: too many requests from this PC, it wants a sign-in. Wait about ten minutes";

    private void CheckBlocked()
    {
        if (Blocked) throw new SpotifyException($"YouTube: still waiting out its request limit ({Math.Ceiling((_blockedUntilUtc - DateTime.UtcNow).TotalMinutes)} min)");
    }

    private static bool IsBotCheck(string stderr) =>
        stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase) || stderr.Contains("HTTP Error 429", StringComparison.Ordinal);

    // Every audio stream that plays without a login comes from one YouTube client ("visionos")
    // at the moment; asking only that one is the fast path, yt-dlp's defaults the fallback.
    // The watch page is still fetched: skipping it got the "not a bot" answer after a dozen requests.
    private static readonly (string How, string[] Args)[] Attempts =
    {
        ("fast", new[] { "--no-js-runtimes", "--extractor-args", "youtube:player_client=visionos;skip=hls,dash,translated_subs" }),
        ("default", Array.Empty<string>()),
    };

    /// <summary>A track's audio stream address. Kept until it expires (about six hours).</summary>
    private async Task<Resolved> ResolveAsync(string id, bool fresh, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!fresh && _resolved.TryGetValue(id, out var had) && had.ValidUntilUtc > DateTime.UtcNow) return had;
            CheckBlocked();
        }
        var exe = YtDlp ?? throw NotSetUp();
        var error = "";
        foreach (var (how, args) in Attempts)
        {
            var all = new List<string> { "--no-playlist", "--no-warnings", "-f", "bestaudio", "-O", "%(.{id,title,artist,channel,uploader,duration,url})j" };
            all.AddRange(args);
            all.Add("https://www.youtube.com/watch?v=" + id);
            var (code, stdout, stderr) = await RunToolAsync(exe, all, ct);
            if (code == 0 && LastJson(stdout) is { } json && ParseItem(json, out var item, out var url) && url.Length > 0)
            {
                var r = new Resolved(item, url, ValidUntil(url));
                lock (_sync)
                {
                    Remember(item);
                    r = r with { Item = Meta(item.Id) };
                    if (_resolved.Count > 200) _resolved.Clear();
                    _resolved[id] = r;
                }
                return r;
            }
            if (IsBotCheck(stderr))
            {
                lock (_sync) _blockedUntilUtc = DateTime.UtcNow + BlockedFor;
                throw new SpotifyException(BlockedMessage);
            }
            error = LastError(stderr, code);
            Log.Info($"YouTube: looking up {id} ({how}) failed: {Short(error, 160)}");
        }
        throw new SpotifyException("YouTube: " + Short(error.Replace("ERROR: ", "").Replace("[youtube] ", ""), 100));
    }

    private static DateTime ValidUntil(string url)
    {
        var m = Regex.Match(url, @"[?&]expire=(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime - TimeSpan.FromMinutes(10)
            : DateTime.UtcNow + TimeSpan.FromHours(5);
    }

    /// <summary>The tracks of a playlist, an album or a radio, without looking up their streams.</summary>
    private async Task<List<Item>> ListAsync(string url, int max, CancellationToken ct)
    {
        lock (_sync) CheckBlocked();
        var exe = YtDlp ?? throw NotSetUp();
        var (code, stdout, stderr) = await RunToolAsync(exe, new[]
        {
            "--flat-playlist", "--no-warnings", "-I", ":" + max, "-O", "%(.{id,title,artist,channel,uploader,duration})j", url,
        }, ct);
        if (IsBotCheck(stderr))
        {
            lock (_sync) _blockedUntilUtc = DateTime.UtcNow + BlockedFor;
            throw new SpotifyException(BlockedMessage);
        }
        var items = new List<Item>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('{') && ParseItem(line, out var item, out _) && item.Id.Length == 11) items.Add(item);
        }
        if (items.Count == 0 && code != 0) throw new SpotifyException("YouTube: " + Short(LastError(stderr, code).Replace("ERROR: ", ""), 100));
        lock (_sync)
        {
            foreach (var i in items) Remember(i);
            return items.Select(i => Meta(i.Id)).ToList();
        }
    }

    private static bool ParseItem(string json, out Item item, out string url)
    {
        item = new Item("", "", "", 0);
        url = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var j = doc.RootElement;
            var id = J.Str(j, "id");
            if (id.Length == 0) return false;
            var artist = new[] { "artist", "channel", "uploader" }.Select(k => J.Str(j, k)).FirstOrDefault(s => s.Length > 0) ?? "";
            if (artist.EndsWith(" - Topic", StringComparison.Ordinal)) artist = artist[..^8];
            var duration = j.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            item = new Item(id, J.Str(j, "title") is { Length: > 0 } t ? t : id, artist, (int)(duration * 1000));
            url = J.Str(j, "url");
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? LastJson(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));

    private static string LastError(string stderr, int code) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? $"yt-dlp exited with {code}";

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>Looks up the next track's stream while this one plays, so "next" is quick.</summary>
    private void Prefetch()
    {
        lock (_sync)
        {
            if (_prefetch is { IsCompleted: false }) return;
            if (_pos + 1 >= _list.Count) return;
            var next = _list[_pos + 1];
            if (_resolved.TryGetValue(next.Id, out var had) && had.ValidUntilUtc > DateTime.UtcNow) return;
            _prefetch = Task.Run(async () =>
            {
                try { await ResolveAsync(next.Id, fresh: false, _life); }
                catch (Exception e) when (e is SpotifyException or OperationCanceledException)
                {
                    Log.Info("YouTube: could not get the next track ready: " + e.Message);
                }
            });
        }
    }

    // ------------------------------------------------------------------ processes

    private static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task<(int Code, string Out, string Err)> RunToolAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = Psi(exe, args);
        psi.StandardOutputEncoding = Encoding.UTF8;
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SpotifyException($"YouTube: could not start {Path.GetFileName(exe)}: {e.Message}");
        }
        using (process)
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return (process.ExitCode, await stdout, await stderr);
            }
            finally { KillProcess(process); }
        }
    }

    /// <summary>ffmpeg writing the track as raw PCM (44.1 kHz, 16-bit, stereo) to its stdout.</summary>
    private static Process StartFfmpeg(string url, double startMs)
    {
        var exe = Ffmpeg ?? throw NotSetUp();
        var args = new List<string> { "-nostdin", "-loglevel", "error" };
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5" });
        args.AddRange(new[] { "-i", url });
        // After the input: ffmpeg reads from the start and drops what comes before, a few tenths
        // of a second for a song. Before the input it would seek over HTTP, which YouTube answers
        // with "400 Bad Request" for the Windows build.
        if (startMs > 0) args.AddRange(new[] { "-ss", (startMs / 1000).ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-vn", "-f", "s16le", "-ar", "44100", "-ac", "2", "-" });
        Process process;
        try { process = Process.Start(Psi(exe, args)) ?? throw new InvalidOperationException("no process"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SpotifyException("YouTube: could not start ffmpeg: " + e.Message);
        }
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("   ffmpeg: " + Short(e.Data.Trim(), 200));
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static void KillProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* already gone */ }
    }

    // ------------------------------------------------------------------ search

    // YouTube Music's own search. yt-dlp can run it too but gives titles only (no artist, no
    // length, playlists without a name), which is useless in a result list.
    private static string MusicBase => Environment.GetEnvironmentVariable("SPOTIFY_RADIO_YTM_BASE") is { Length: > 0 } b ? b : "https://music.youtube.com";
    private const string SongsFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string AlbumsFilter = "EgWKAQIYAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string PlaylistsFilter = "EgWKAQIoAWoKEAkQBRAKEAMQBA%3D%3D";

    public async Task<List<SearchItem>> SearchAsync(string query, CancellationToken ct)
    {
        if (!Available) throw NotSetUp();
        if (query.Length == 0) throw new SpotifyException("Empty search");
        var songs = SearchSectionAsync(query, SongsFilter, ct);
        var albums = SearchSectionAsync(query, AlbumsFilter, ct);
        var lists = SearchSectionAsync(query, PlaylistsFilter, ct);
        var items = new List<SearchItem>();
        var limit = Math.Clamp(_cfg.SearchLimit, 1, 10);
        items.AddRange((await songs).Where(i => i.Kind == "track").Take(limit));
        items.AddRange((await Optional(albums, "albums")).Where(i => i.Kind == "album").Take(5));
        items.AddRange((await Optional(lists, "playlists")).Where(i => i.Kind == "playlist").Take(5));
        return items;
    }

    /// <summary>Albums and playlists are extras: without them the songs are still shown.</summary>
    private static async Task<List<SearchItem>> Optional(Task<List<SearchItem>> section, string what)
    {
        try { return await section; }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Info($"YouTube: searching {what} failed: {e.InnerException?.Message ?? e.Message}");
            return new List<SearchItem>();
        }
    }

    /// <summary>One part of a search (songs, albums or playlists). A request that fails on its way is tried once more.</summary>
    private async Task<List<SearchItem>> SearchSectionAsync(string query, string filter, CancellationToken ct)
    {
        try { return await SearchOnceAsync(query, filter, ct); }
        catch (HttpRequestException e)
        {
            Log.Info($"YouTube: a search request failed ({e.InnerException?.Message ?? e.Message}), trying again.");
            await Task.Delay(300, ct);
            return await SearchOnceAsync(query, filter, ct);
        }
    }

    private async Task<List<SearchItem>> SearchOnceAsync(string query, string filter, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20260101.01.00", hl = "en" } },
            query,
            @params = filter,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, MusicBase + "/youtubei/v1/search?prettyPrint=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new SpotifyException($"YouTube Music search failed ({(int)response.StatusCode})");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var items = new List<SearchItem>();
        var rows = 0;
        foreach (var row in FindAll(doc.RootElement, "musicResponsiveListItemRenderer"))
        {
            rows++;
            var columns = J.Arr(row, "flexColumns").Select(ColumnText).ToList();
            if (columns.Count == 0 || columns[0].Length == 0) continue;
            // second column: "Radiohead • Pablo Honey • 3:59", "Album • Radiohead • 1997", "Someone • 462 views"
            var parts = columns.Count > 1
                ? columns[1].Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            var videoId = J.Obj(row, "playlistItemData") is { } data ? J.Str(data, "videoId") : "";
            var browseId = J.Obj(row, "navigationEndpoint") is { } nav
                ? FindAll(nav, "browseId").Where(b => b.ValueKind == JsonValueKind.String).Select(b => b.GetString() ?? "").FirstOrDefault() ?? ""
                : "";
            if (filter == SongsFilter && videoId.Length > 0)
            {
                var artist = parts.Length > 0 ? parts[0] : "";
                var length = parts.Length > 1 && parts[^1].Contains(':') ? parts[^1] : "";
                lock (_sync) Remember(new Item(videoId, columns[0], artist, ParseLength(length)));
                items.Add(new SearchItem("track", VideoPrefix + videoId, columns[0], length.Length > 0 ? $"{artist} ({length})" : artist));
            }
            else if (filter == AlbumsFilter && browseId.StartsWith("MPRE", StringComparison.Ordinal))
            {
                items.Add(new SearchItem("album", AlbumPrefix + browseId, columns[0], string.Join(", ", parts.Skip(1))));
            }
            else if (filter == PlaylistsFilter && browseId.StartsWith("VL", StringComparison.Ordinal))
            {
                items.Add(new SearchItem("playlist", PlaylistPrefix + browseId[2..], columns[0], parts.Length > 0 ? parts[0] : ""));
            }
        }
        if (rows > 0 && items.Count == 0) Log.Info($"YouTube: {rows} search results in a form this daemon does not know (filter {filter[..10]})");
        return items;
    }

    private static int ParseLength(string text)
    {
        var total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part, out var n)) return 0;
            total = total * 60 + n;
        }
        return total * 1000;
    }

    private static string ColumnText(JsonElement column) =>
        J.Obj(column, "musicResponsiveListItemFlexColumnRenderer") is { } r && J.Obj(r, "text") is { } t
            ? string.Concat(J.Arr(t, "runs").Select(run => J.Str(run, "text")))
            : "";

    private static IEnumerable<JsonElement> FindAll(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals(name)) yield return p.Value;
                else foreach (var inner in FindAll(p.Value, name)) yield return inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                foreach (var inner in FindAll(item, name)) yield return inner;
        }
    }
}
