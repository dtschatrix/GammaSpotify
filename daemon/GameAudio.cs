using System.Diagnostics;

namespace SpotifyRadio;

/// <summary>
/// Plays Spotify through the game engine.
///
/// librespot (an open-source Spotify Connect client) runs as a child process and shows up in
/// Spotify as its own device. Its audio comes out of its stdout as raw PCM instead of going to
/// the speakers. While the game has asked for a stream, that PCM is cut into short OGG Vorbis
/// files which are dropped into the game's level folder and announced to the game, which chains
/// them into one continuous sound.
/// </summary>
public sealed class GameAudio
{
    private const int SampleRate = 44100;
    private const int InChannels = 2;                 // librespot always outputs stereo
    private const int FrameBytes = 2 * InChannels;    // 16-bit samples
    private const int HistoryBytes = 4096 * FrameBytes;
    public const string MovedFolder = "spotify_radio"; // under gamedata\sounds; must match the game script
    public const string ChunkPrefix = "spr_";          // chunk files written into the game's level folder

    private readonly Config _cfg;
    private readonly SpotifyClient _spotify;
    private readonly Func<string[], CancellationToken, Task> _broadcast;
    private readonly Func<TimeSpan> _gameQuiet;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _reactGate = new(1, 1);
    private Process? _process;

    // current stream (guarded by _gate)
    private bool _active;
    private string _outDir = "";
    private string _session = "";
    private int _outChannels = 1;
    private int _index;
    private bool _alignPending;
    private long _totalBytes;
    private readonly MemoryStream _pending = new();
    private byte[] _history = Array.Empty<byte>();
    private string? _previousDeviceId;
    private volatile bool _resetPace;  // the game holds nothing: read ahead again
    private volatile bool _draining;   // a track change was asked for: what still arrives is the old track
    private DateTime _drainUntilUtc;
    private volatile bool _paused;     // the game was told Spotify is paused
    private volatile bool _stalled;    // librespot is giving no audio right now
    private PlayerState? _lastState;   // last Spotify state seen, and when
    private DateTime _lastStateUtc;
    private int _gameBufMs = -1;       // audio the game says it holds, until used
    private volatile bool _gameHolds;
    private volatile int _gameLastIndex;
    private volatile bool _away;       // the game stopped talking
    private DateTime _awaySinceUtc;
    private int _awayPaused;           // 1: Spotify was paused because of that
    private volatile bool _dropped;    // the stream was stopped while the game was away
    private volatile bool _needMark;   // the game has not been told what this segment is
    private DateTime _segStartUtc;     // when the first audio of this segment was read
    private int _segStartIndex;        // and the chunk it went into
    private string _markUri = "";      // track the newest chunks belong to
    private DateTime _pendingStartUtc; // when the chunk being filled was started
    private readonly DateTime[] _chunkReadUtc = new DateTime[64]; // same, for the chunks before it

    public GameAudio(Config cfg, SpotifyClient spotify, Func<string[], CancellationToken, Task> broadcast, Func<TimeSpan> gameQuiet)
    {
        _cfg = cfg;
        _spotify = spotify;
        _broadcast = broadcast;
        _gameQuiet = gameQuiet;
    }

    private string ExePath => !string.IsNullOrWhiteSpace(_cfg.LibrespotPath)
        ? _cfg.LibrespotPath
        : Path.Combine(Config.AppDir, "librespot", OperatingSystem.IsWindows() ? "librespot.exe" : "librespot");

    private string CacheDir => Path.Combine(Config.HomeDir, "librespot-cache");

    public bool Installed => File.Exists(ExePath);
    public bool Streaming => _active;

    // ------------------------------------------------------------------ librespot process

    /// <summary>Keeps librespot running for as long as the daemon runs.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!Installed)
        {
            Log.Info($"Game audio: off ({ExePath} not found).");
            return;
        }
        _ = Task.Run(() => WatchGameAsync(ct), CancellationToken.None);
        var quickExits = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.Info("Game audio: librespot failed: " + e.Message); }

            if (ct.IsCancellationRequested) break;
            quickExits = started.Elapsed < TimeSpan.FromSeconds(20) ? quickExits + 1 : 0;
            if (quickExits >= 3)
            {
                Log.Info("Game audio: librespot keeps exiting, giving up. The remote-control tab still works.");
                return;
            }
            Log.Info("Game audio: librespot stopped, restarting in 5 s.");
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }
        Kill();
    }

    private string CredentialsPath => Path.Combine(CacheDir, "credentials.json");

    private Process StartLibrespot(bool login)
    {
        Directory.CreateDirectory(CacheDir);
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (_cfg.LibrespotArgs is { Length: > 0 } custom)
        {
            foreach (var a in custom) psi.ArgumentList.Add(a);
        }
        else
        {
            foreach (var a in new[]
                     {
                         "--name", _cfg.StreamDeviceName, "--device-type", "speaker",
                         "--backend", "pipe", "--format", "S16", "--bitrate", "320",
                         "--initial-volume", "100", "--cache", CacheDir, "--disable-audio-cache",
                         "--disable-discovery",
                     })
                psi.ArgumentList.Add(a);
            if (login) psi.ArgumentList.Add("--enable-oauth");
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start librespot");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("   librespot: " + e.Data.Trim());
        };
        process.BeginErrorReadLine();
        return process;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var custom = _cfg.LibrespotArgs is { Length: > 0 };
        if (!custom && !File.Exists(CredentialsPath))
        {
            await LoginAsync(ct);
            if (!File.Exists(CredentialsPath)) throw new InvalidOperationException("the Spotify login for game audio was not completed");
        }

        using var process = StartLibrespot(login: false);
        _process = process;
        Log.Info($"Game audio: ready, Spotify device \"{_cfg.StreamDeviceName}\".");
        try
        {
            await PumpAsync(process.StandardOutput.BaseStream, ct);
        }
        finally
        {
            Kill();
            _process = null;
        }
    }

    /// <summary>
    /// One-time sign-in for librespot. In this mode librespot prints the login address on stdout,
    /// which is otherwise the audio pipe, so sign-in runs as its own short-lived process: open the
    /// address, wait for librespot to save its credentials, stop it.
    /// </summary>
    private async Task LoginAsync(CancellationToken ct)
    {
        Log.Info("Game audio: first run - Spotify needs one more login, for the in-game audio device.");
        using var process = StartLibrespot(login: true);
        _process = process;
        try
        {
            var opened = false;
            var reader = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    var at = line.IndexOf("https://", StringComparison.Ordinal);
                    if (at < 0 || opened) continue;
                    opened = true;
                    var url = line[at..].Trim();
                    Log.Info("Game audio: opening the Spotify login page. If the browser does not open, paste this URL:");
                    Console.WriteLine(url);
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception e) { Log.Info("Could not open the browser: " + e.Message); }
                }
            }, ct);

            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(CredentialsPath) && !process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(500, ct);
            if (File.Exists(CredentialsPath))
            {
                await Task.Delay(1000, ct); // let it finish writing
                Log.Info("Game audio: logged in.");
            }
        }
        finally
        {
            Kill();
            _process = null;
        }
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* already gone */ }
    }

    private const int StallMs = 250; // no audio for this long while streaming: find out why

    /// <summary>
    /// Reads PCM from librespot. Its pipe output is as fast as the reader, so the reader sets the
    /// pace: real time when nobody listens; while the game is streaming, as fast as it comes until
    /// the game holds LeadMs of audio, so that after a start or a track change the game has its
    /// buffer at once. The game reports what it holds ("buf"), which corrects the estimate.
    /// </summary>
    private async Task PumpAsync(Stream pcm, CancellationToken ct)
    {
        var buffer = new byte[FrameBytes * (SampleRate / 20)]; // 50 ms
        var clock = Stopwatch.StartNew();
        double aheadMs = 0;                      // audio the game holds: read minus heard
        var last = clock.Elapsed.TotalMilliseconds;
        const double bytesPerMs = SampleRate * FrameBytes / 1000.0;
        var wasFilling = false;
        double trustReportsAt = 0;
        // Reading ahead is also limited by a budget, whatever the game reports: the lead plus a
        // little, refilled slowly. The game can never be flooded with chunks.
        double budgetCap = Math.Max(0, _cfg.LeadMs) + 2000, budgetMs = budgetCap;
        while (true)
        {
            var before = clock.Elapsed.TotalMilliseconds;
            var read = pcm.ReadAsync(buffer, ct).AsTask();
            var stalled = false;
            if (_active && await Task.WhenAny(read, Task.Delay(StallMs, ct)) != read)
            {
                stalled = _stalled = true;
                await OnStallAsync(ct);
            }
            var n = await read;
            _stalled = false;
            if (n == 0) return; // librespot exited
            var now = clock.Elapsed.TotalMilliseconds;
            var blocked = now - before;
            var passed = now - last - (blocked > 30 ? blocked : 0); // nothing is heard while nothing comes
            aheadMs -= passed;
            budgetMs = Math.Min(budgetCap, budgetMs + passed * 1.05 - n / bytesPerMs);
            last = now;

            if (stalled) await OnAudioBackAsync(ct);
            var filling = _active && !_gameHolds;   // otherwise: plain real time
            if (_resetPace || filling && !wasFilling) budgetMs = budgetCap;
            if (_resetPace || filling != wasFilling)
            {
                aheadMs = 0;
                trustReportsAt = now + 700;      // a report sent before the game dropped its buffer may still arrive
            }
            _resetPace = false;
            wasFilling = filling;

            await OnPcmAsync(buffer.AsMemory(0, n), ct);
            aheadMs += n / bytesPerMs;

            // What the game says it holds replaces the estimate. It also says which chunk it got
            // last, so the ones still on their way there are counted too.
            var reported = Interlocked.Exchange(ref _gameBufMs, -1);
            var onTheWay = _index - 1 - _gameLastIndex;
            if (filling && reported >= 0 && onTheWay is >= 0 and < 30 && now >= trustReportsAt)
                aheadMs = reported + onTheWay * _cfg.ChunkMs + _pending.Length / bytesPerMs;

            var limit = filling ? Math.Max(0, _cfg.LeadMs) : 0;
            if (aheadMs < -500) aheadMs = -500;  // never owe more than this: no long catch-up bursts
            // Above the limit, slow down to half speed at most rather than stop: librespot handles
            // pause and skip only between writes, so it must never be left blocked for long.
            if (aheadMs > limit + 120) await Task.Delay((int)Math.Min(aheadMs - limit - 100, 100), ct);
            else if (budgetMs < 0) await Task.Delay((int)Math.Min(-budgetMs + 20, 100), ct);
        }
    }

    /// <param name="holds">The game is not playing what it gets (muted): it only wants the newest audio.</param>
    public void OnGameBuffer(int ms, int lastIndex, bool holds)
    {
        _gameHolds = holds;
        _gameLastIndex = lastIndex;
        Interlocked.Exchange(ref _gameBufMs, Math.Max(0, ms));
    }

    // ------------------------------------------------------------------ the game going away

    /// <summary>
    /// The game stops talking while it is paused, in the main menu or loading. Spotify is paused
    /// then, and resumed when the game is back, so the music continues where it stopped: the game
    /// keeps the chunks it holds.
    /// </summary>
    private async Task WatchGameAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, ct);
                var quiet = _gameQuiet() > TimeSpan.FromSeconds(Math.Max(2, _cfg.AwaySeconds));
                if (!quiet && _dropped)
                {
                    _dropped = false;
                    await _broadcast(new[] { "stream", "ended" }, ct); // the game asks again
                }
                if (!_active) continue;
                if (quiet && !_away)
                {
                    _awaySinceUtc = DateTime.UtcNow;
                    _away = true;
                    if ((await _spotify.GetStateAsync(ct)).Status == "playing")
                    {
                        Log.Info("Game audio: the game is not answering, pausing Spotify.");
                        Interlocked.Exchange(ref _awayPaused, 1);
                        await _spotify.PauseAsync(ct);
                    }
                }
                else if (!quiet && _away)
                {
                    _away = false;
                    if (Interlocked.Exchange(ref _awayPaused, 0) == 1)
                    {
                        Log.Info("Game audio: the game is back, resuming Spotify.");
                        await _spotify.PlayAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException)
            {
                Log.Info("Game audio: " + e.Message);
            }
        }
    }

    // ------------------------------------------------------------------ pauses and track changes

    /// <summary>
    /// The game holds several seconds of audio. When Spotify is paused it must stop at once and
    /// keep them; when the track is changed by hand it must drop them. Both show up here as the
    /// audio stopping, so Spotify is asked which it is.
    /// </summary>
    private async Task OnStallAsync(CancellationToken ct)
    {
        if (_draining)
        {
            await FlushAsync("new track", ct); // the gap before the new track: everything after it is new
            return;
        }
        if (_away) return;
        try
        {
            await ReactAsync(await _spotify.GetStateAsync(ct), ct);
        }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException)
        {
            Log.Info("Game audio: could not ask Spotify why the audio stopped: " + e.Message);
        }
    }

    /// <summary>Audio is flowing again after a stall.</summary>
    private async Task OnAudioBackAsync(CancellationToken ct)
    {
        if (_draining)
        {
            await FlushAsync("new track", ct); // a change was asked for while paused: this is the new track
            return;
        }
        if (!_paused)
        {
            // A skip or seek made elsewhere also shows up as a gap. Ask without holding the audio up.
            _ = Task.Run(async () =>
            {
                try { await ReactAsync(await _spotify.GetStateAsync(ct), ct); }
                catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException or OperationCanceledException) { }
            }, CancellationToken.None);
            return;
        }
        // Resuming: the game either carries on with what it kept, or drops it if this is something else now.
        _paused = false;
        PlayerState? state = null;
        try { state = await _spotify.GetStateAsync(ct); }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException) { }

        if (state is { Status: "playing" } s && !_needMark && (OtherTrack(s) && !NearEnd() || Jumped(s)))
            await FlushAsync("track changed", ct);
        else
            await _broadcast(new[] { "audio", "resume" }, ct);
        if (state is not null) Remember(state);
    }

    /// <summary>Called with every state the daemon polls, and when the audio stops.</summary>
    public async Task OnPolledStateAsync(PlayerState state, CancellationToken ct)
    {
        if (_active && !_draining) await ReactAsync(state, ct);
        else Remember(state);
    }

    private async Task ReactAsync(PlayerState state, CancellationToken ct)
    {
        // one at a time: the poll, a gap in the audio and a new segment can all ask at once
        await _reactGate.WaitAsync(ct);
        try { await ReactOnceAsync(state, ct); }
        finally { _reactGate.Release(); }
    }

    private async Task ReactOnceAsync(PlayerState state, CancellationToken ct)
    {
        if (state.Status != "playing")
        {
            // At the end of a track with nothing after it the audio stops too, but then the game
            // should play out what it holds.
            if (_stalled && !_paused && !_away && !NearEnd())
            {
                _paused = true;
                Log.Info("Game audio: Spotify is paused.");
                await _broadcast(new[] { "audio", "hold" }, ct);
            }
        }
        else if (_needMark) await MarkSegmentAsync(state, ct);
        else if (OtherTrack(state))
        {
            if (NearEnd()) await MarkNextTrackAsync(state, ct);
            else if ((DateTime.UtcNow - _segStartUtc).TotalSeconds < 6)
            {
                _needMark = true;                // the first answer after a change still named the old track
                await MarkSegmentAsync(state, ct);
            }
            else await FlushAsync("track changed", ct);
        }
        else if (Jumped(state)) await FlushAsync("position changed", ct);
        Remember(state);
    }

    private void Remember(PlayerState state)
    {
        _lastState = state;
        _lastStateUtc = DateTime.UtcNow;
    }

    private bool OtherTrack(PlayerState state) => state.Uri.Length > 0 && _markUri.Length > 0 && state.Uri != _markUri;

    /// <summary>The track last seen playing has reached its end, give or take what was read ahead.</summary>
    private bool NearEnd() =>
        _lastState is { DurationMs: > 0, Status: "playing" } was
        && was.ProgressMs + (DateTime.UtcNow - _lastStateUtc).TotalMilliseconds >= was.DurationMs - _cfg.LeadMs - 4000;

    /// <summary>The same track, somewhere else entirely (a seek).</summary>
    private bool Jumped(PlayerState state)
    {
        if (_lastState is not { } was || state.Uri != was.Uri || state.Uri.Length == 0) return false;
        var expected = was.ProgressMs + (was.Status == "playing" ? (DateTime.UtcNow - _lastStateUtc).TotalMilliseconds : 0);
        // After a pause librespot reports where it really is, which is ahead by what was read ahead.
        return Math.Abs(state.ProgressMs - expected) > _cfg.LeadMs + 4000;
    }

    /// <summary>The game asked for another track: what arrives until the gap (or for a moment) is the old one.</summary>
    public void ExpectChange()
    {
        if (!_active) return;
        _drainUntilUtc = DateTime.UtcNow.AddMilliseconds(2500);
        _draining = true;
        _resetPace = true;                       // read on at full speed, so the gap is seen at once
        _lastState = null;
    }

    /// <summary>Starts a new segment: the game drops what it holds and gets a fresh buffer quickly.</summary>
    private async Task FlushAsync(string why, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_active) return;
            NewSegment();
        }
        finally { _gate.Release(); }
        Log.Info($"Game audio: {why}, starting over.");
        await _broadcast(new[] { "audio", "flush" }, ct);
    }

    private void NewSegment()
    {
        _draining = false;
        _paused = false;
        _pending.SetLength(0);
        _history = Array.Empty<byte>();
        _alignPending = true;
        _resetPace = true;
        _needMark = true;
        _segStartUtc = DateTime.MinValue;
        _markUri = "";
    }

    // ------------------------------------------------------------------ which track a chunk belongs to

    // Spotify's position is "where it was last reported plus the time since", whatever speed the
    // audio is read at. So the audio read at the moment of a report is at the reported position,
    // and that ties chunks to positions: the game is told "from chunk N on it is this track, and
    // chunk N starts at P ms" and shows the track and time of the chunk it is playing.

    /// <summary>After a start or a flush: the first audio of the segment was read when playback (re)started.</summary>
    private async Task MarkSegmentAsync(PlayerState state, CancellationToken ct)
    {
        int index;
        double pos;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_active || !_needMark || _segStartUtc == DateTime.MinValue || state.Status != "playing" || state.Uri.Length == 0) return;
            _needMark = false;
            _markUri = state.Uri;
            index = _segStartIndex;
            pos = state.ProgressMs - (DateTime.UtcNow - _segStartUtc).TotalMilliseconds;
        }
        finally { _gate.Release(); }
        await SendMarkAsync(index, pos, state, ct);
    }

    /// <summary>The next track began without a break: its first audio was read ProgressMs ago.</summary>
    private async Task MarkNextTrackAsync(PlayerState state, CancellationToken ct)
    {
        int index;
        double pos;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_active) return;
            var began = DateTime.UtcNow.AddMilliseconds(-state.ProgressMs);
            index = -1;
            pos = 0;
            for (var i = Math.Max(_segStartIndex, _index - _chunkReadUtc.Length + 1); i < _index; i++)
            {
                var readAt = _chunkReadUtc[i % _chunkReadUtc.Length];
                if (readAt < began) continue;
                index = i;
                pos = (readAt - began).TotalMilliseconds;
                break;
            }
            if (index < 0)
            {
                // it began in the chunk being filled, or later
                index = _pending.Length > 0 && _pendingStartUtc < began ? _index + 1 : _index;
                pos = (_pendingStartUtc - began).TotalMilliseconds + (index - _index) * _cfg.ChunkMs;
            }
            _markUri = state.Uri;
        }
        finally { _gate.Release(); }
        Log.Info("Game audio: next track.");
        await SendMarkAsync(index, pos, state, ct);
    }

    private Task SendMarkAsync(int index, double pos, PlayerState s, CancellationToken ct) =>
        _broadcast(new[]
        {
            "track", index.ToString(), ((int)Math.Clamp(pos, 0, Math.Max(0, s.DurationMs))).ToString(),
            s.DurationMs.ToString(), s.Uri, s.Artist, s.Title,
        }, ct);

    /// <summary>Asks Spotify what a new segment is, shortly after its first audio.</summary>
    private async Task ResolveMarkAsync(int segIndex, CancellationToken ct)
    {
        try
        {
            foreach (var wait in new[] { 400, 800, 1500 })
            {
                await Task.Delay(wait, ct);
                if (!_active || !_needMark || _segStartIndex != segIndex) return;
                await ReactAsync(await _spotify.GetStateAsync(ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ chunking

    private async Task OnPcmAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var phase = (int)(_totalBytes % FrameBytes);
            _totalBytes += data.Length;
            if (!_active) return;
            if (_draining && DateTime.UtcNow < _drainUntilUtc) return; // still the old track

            if (_away && DateTime.UtcNow - _awaySinceUtc > TimeSpan.FromSeconds(10))
            {
                // The game is gone and something is playing on this device anyway. Nobody is
                // there to take the chunks; the game asks for the stream again when it is back.
                Log.Info("Game audio: the game is gone, stream stopped.");
                _active = false;
                _dropped = true;
                _pending.SetLength(0);
                Interlocked.Exchange(ref _awayPaused, 0); // whoever started it again decides now
                return;
            }

            if (_draining)
            {
                // No gap came: the new track started straight away. Start over from here.
                NewSegment();
                await _broadcast(new[] { "audio", "flush" }, ct);
            }
            if (_alignPending)
            {
                // a stream can start in the middle of a stereo frame: skip to the next frame boundary
                var skip = (FrameBytes - phase) % FrameBytes;
                if (skip >= data.Length) return;
                data = data[skip..];
                _alignPending = false;
            }
            var now = DateTime.UtcNow;
            if (_pending.Length == 0) _pendingStartUtc = now;
            if (_segStartUtc == DateTime.MinValue)
            {
                _segStartUtc = now;
                _segStartIndex = _index;
                var seg = _index;
                _ = Task.Run(() => ResolveMarkAsync(seg, ct), CancellationToken.None);
            }
            _pending.Write(data.Span);

            var chunkBytes = (int)((long)SampleRate * _cfg.ChunkMs / 1000) * FrameBytes; // whole frames
            while (_pending.Length >= chunkBytes)
            {
                var all = _pending.ToArray();
                _chunkReadUtc[_index % _chunkReadUtc.Length] = _pendingStartUtc;
                await EmitAsync(all.AsMemory(0, chunkBytes), ct);
                _history = all.AsSpan(Math.Max(0, chunkBytes - HistoryBytes), Math.Min(chunkBytes, HistoryBytes)).ToArray();
                _pending.SetLength(0);
                _pending.Write(all, chunkBytes, all.Length - chunkBytes);
                _pendingStartUtc = now;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task EmitAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        // Not configurable on purpose: the encoder library produces corrupt audio at most quality
        // settings (0.3, 0.4, 0.6, 0.7 mono; 0.8 and 1.0 stereo). These two were verified by decoding.
        var quality = _outChannels == 1 ? 0.8f : 0.5f;
        var ogg = VorbisChunkEncoder.Encode(_history, pcm.Span, InChannels, _outChannels, SampleRate, quality);
        var name = $"{_session}_{_index:00000}";
        var tmp = Path.Combine(_outDir, name + ".tmp");
        await File.WriteAllBytesAsync(tmp, ogg, ct);
        File.Move(tmp, Path.Combine(_outDir, name + ".ogg"), overwrite: true); // appears complete or not at all
        await _broadcast(new[] { "chunk", name, _cfg.ChunkMs.ToString(), _index.ToString() }, ct);
        _index++;
    }

    // ------------------------------------------------------------------ start / stop

    /// <summary>Resolves the folder the game asked for; only folders inside the game's gamedata are accepted.</summary>
    public string? ResolveGameDir(string gameDir)
    {
        var gameData = _cfg.ResolveGameDataDir();
        if (gameData is null || gameDir.Length == 0) return null;
        var sep = Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(gameDir.Replace('/', sep).Replace('\\', sep));
        var root = Path.GetFullPath(gameData).TrimEnd(sep) + sep;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Deletes chunk files left behind by earlier streams.</summary>
    public void CleanStale(string outDir)
    {
        var gameData = _cfg.ResolveGameDataDir()!;
        Directory.CreateDirectory(outDir);
        var playedDir = Path.Combine(gameData, "sounds", MovedFolder);
        Directory.CreateDirectory(playedDir);
        var levelsDir = Path.Combine(gameData, "levels");
        var stale = (Directory.Exists(levelsDir) ? Directory.GetFiles(levelsDir, ChunkPrefix + "*", SearchOption.AllDirectories) : Array.Empty<string>())
            .Concat(Directory.GetFiles(outDir, ChunkPrefix + "*"))
            .Concat(Directory.GetFiles(playedDir, "*.ogg"));
        foreach (var old in stale.Distinct())
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <param name="mode">"world" = mono (the game can only position mono sound), "head" = stereo.</param>
    public async Task StartStreamAsync(string mode, string gameDir, CancellationToken ct)
    {
        if (!Installed) throw new SpotifyException("Game audio needs librespot.exe in the daemon's librespot folder");
        if (_process is null or { HasExited: true }) throw new SpotifyException("Game audio is not running yet - check the daemon window");
        var outDir = ResolveGameDir(gameDir) ?? throw new SpotifyException($"Folder \"{gameDir}\" is not inside the game's gamedata");

        // Find our device and move playback onto it.
        DeviceInfo? ours = null;
        for (var attempt = 0; attempt < 6 && ours is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(1000, ct);
            ours = (await _spotify.GetDevicesAsync(ct)).FirstOrDefault(d => d.Name == _cfg.StreamDeviceName);
        }
        if (ours is null)
            throw new SpotifyException($"Spotify does not list \"{_cfg.StreamDeviceName}\" yet - finish the librespot login in the browser");

        var devices = await _spotify.GetDevicesAsync(ct);
        var previous = devices.FirstOrDefault(d => d.Active && d.Id != ours.Id);
        bool resume;

        await _gate.WaitAsync(ct);
        try
        {
            CleanStale(outDir);
            _outDir = outDir;
            _outChannels = mode == "head" ? 2 : 1;
            _session = ChunkPrefix + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x");
            _index = 0;
            _pending.SetLength(0);
            _history = Array.Empty<byte>();
            _alignPending = true;
            NewSegment();
            _lastState = null;
            _away = false;
            _dropped = false;
            Interlocked.Exchange(ref _gameBufMs, -1);
            _gameHolds = false;
            if (previous is not null) _previousDeviceId = previous.Id;
            _active = true;
            resume = Interlocked.Exchange(ref _awayPaused, 0) == 1; // only a pause of ours is undone
        }
        finally { _gate.Release(); }

        // Moving playback keeps it playing or paused as it was.
        if (!ours.Active) await _spotify.TransferAsync(ours.Id, play: resume, ct);
        else if (resume) await _spotify.PlayAsync(ct);
        Log.Info($"Game audio: streaming to the game ({mode}, {_cfg.ChunkMs} ms chunks) -> {outDir}");
    }

    public async Task StopStreamAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        bool wasActive;
        try
        {
            wasActive = _active;
            _active = false;
            _dropped = false;
            _pending.SetLength(0);
        }
        finally { _gate.Release(); }
        if (wasActive)
        {
            Log.Info("Game audio: stream stopped.");
            await RestoreDeviceAsync(ct);
        }
    }

    /// <summary>Hands playback back to the device that had it before the stream started.</summary>
    private async Task RestoreDeviceAsync(CancellationToken ct)
    {
        var id = _previousDeviceId;
        if (id is null) return;
        try
        {
            var devices = await _spotify.GetDevicesAsync(ct);
            if (devices.Any(d => d.Id == id)) await _spotify.TransferAsync(id, play: false, ct);
        }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException)
        {
            Log.Info("Game audio: could not hand playback back: " + e.Message);
        }
    }
}
