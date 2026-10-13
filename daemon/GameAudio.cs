using System.Diagnostics;

namespace SpotifyRadio;

/// <summary>
/// Plays an audio source through the game engine.
///
/// A source (Spotify through librespot: SpotifySource) hands over raw PCM. While the game has
/// asked for a stream, that PCM is cut into short OGG Vorbis files which are dropped into the
/// game's level folder and announced to the game, which chains them into one continuous sound.
///
/// This class does not know where the audio comes from. It reads at the pace the game needs,
/// cuts and announces the chunks, and notices when the game stops answering. What a silence in
/// the audio means and which track a chunk belongs to is asked of the source (IAudioSource).
/// </summary>
public sealed class GameAudio
{
    private const int SampleRate = 44100;
    private const int InChannels = 2;                 // a source always gives stereo
    private const int FrameBytes = 2 * InChannels;    // 16-bit samples
    private const int HistoryBytes = 4096 * FrameBytes;
    public const string MovedFolder = "spotify_radio"; // under gamedata\sounds; must match the game script
    public const string ChunkPrefix = "spr_";          // chunk files written into the game's level folder

    private readonly Config _cfg;
    private readonly IAudioSource _source;
    private readonly Func<string[], CancellationToken, Task> _broadcast;
    private readonly Func<TimeSpan> _gameQuiet;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
    private volatile bool _resetPace;  // the game holds nothing: read ahead again
    private volatile bool _draining;   // a track change was asked for: what still arrives is the old track
    private DateTime _drainUntilUtc;
    private volatile bool _held;       // the game was told the source is paused
    private volatile bool _stalled;    // the source is giving no audio right now
    private int _gameBufMs = -1;       // audio the game says it holds, until used
    private volatile bool _gameHolds;
    private volatile bool _reported;   // a buffer report came since the game went away
    private volatile int _gameLastIndex;
    private volatile bool _away;       // the game stopped talking
    private DateTime _awaySinceUtc;
    private int _awayPaused;           // 1: the source was paused because of that
    private volatile bool _dropped;    // the stream was stopped while the game was away
    private DateTime _segStartUtc;     // when the first audio of this segment was read
    private int _segStartIndex;        // and the chunk it went into
    private DateTime _pendingStartUtc; // when the chunk being filled was started
    private readonly DateTime[] _chunkReadUtc = new DateTime[64]; // same, for the chunks before it

    public GameAudio(Config cfg, IAudioSource source, Func<string[], CancellationToken, Task> broadcast, Func<TimeSpan> gameQuiet)
    {
        _cfg = cfg;
        _source = source;
        _broadcast = broadcast;
        _gameQuiet = gameQuiet;
        source.Attach(this);
    }

    public bool Available => _source.Available;
    public bool Streaming => _active;

    /// <summary>Runs the source for as long as the daemon runs.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!_source.Available)
        {
            Log.Info($"Game audio: off ({_source.Missing}).");
            return;
        }
        _ = Task.Run(() => WatchGameAsync(ct), CancellationToken.None);
        await _source.RunAsync(ct);
    }

    public void Kill() => _source.Kill();

    // ------------------------------------------------------------------ what a source may ask and do

    /// <summary>The source is giving no audio right now.</summary>
    public bool Stalled => _stalled;

    /// <summary>The game stopped talking (paused, main menu, loading, closed).</summary>
    public bool Away => _away;

    /// <summary>The game was told to stop and keep what it holds. Set by the source; a new segment clears it.</summary>
    public bool Held
    {
        get => _held;
        set => _held = value;
    }

    /// <summary>A change was announced with <see cref="BeginDrain"/> and the new audio has not started yet.</summary>
    public bool Draining => _draining;

    /// <summary>When the first audio of the current segment was read; DateTime.MinValue before that.</summary>
    public DateTime SegmentStartUtc => _segStartUtc;

    /// <summary>The chunk the first audio of the current segment went into.</summary>
    public int SegmentStartIndex => _segStartIndex;

    public readonly struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => _gate.Release();
    }

    /// <summary>
    /// The stream's lock. A source keeps notes per segment (which track it is, whether the game
    /// has been told); those are reset under this lock when a segment starts, so they are also
    /// changed under it.
    /// </summary>
    public async Task<Releaser> LockAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    /// <summary>Tells the game to stop at once and keep what it has not played.</summary>
    public Task SendHoldAsync(CancellationToken ct) => _broadcast(new[] { "audio", "hold" }, ct);

    /// <summary>Tells the game to carry on with what it kept.</summary>
    public Task SendResumeAsync(CancellationToken ct) => _broadcast(new[] { "audio", "resume" }, ct);

    /// <summary>
    /// The source was asked for something else: what arrives until its audio pauses (or for
    /// <paramref name="ms"/> at most) is still the old audio and is dropped.
    /// </summary>
    public void BeginDrain(int ms)
    {
        _drainUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);
        _draining = true;
        _resetPace = true;                       // read on at full speed, so the gap is seen at once
    }

    /// <summary>Starts a new segment: the game drops what it holds and gets a fresh buffer quickly.</summary>
    public async Task FlushAsync(string why, CancellationToken ct)
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

    /// <summary>
    /// Which chunk the audio read at <paramref name="began"/> went into, and how far into the
    /// audio since then that chunk starts (ms). Call under the lock.
    /// </summary>
    public (int Index, double PosMs) ChunkReadFrom(DateTime began)
    {
        for (var i = Math.Max(_segStartIndex, _index - _chunkReadUtc.Length + 1); i < _index; i++)
        {
            var readAt = _chunkReadUtc[i % _chunkReadUtc.Length];
            if (readAt < began) continue;
            return (i, (readAt - began).TotalMilliseconds);
        }
        // it began in the chunk being filled, or later
        var index = _pending.Length > 0 && _pendingStartUtc < began ? _index + 1 : _index;
        return (index, (_pendingStartUtc - began).TotalMilliseconds + (index - _index) * _cfg.ChunkMs);
    }

    /// <summary>Tells the game: from chunk <paramref name="index"/> on it is this track, and that chunk starts at <paramref name="posMs"/>.</summary>
    public Task MarkAsync(int index, double posMs, PlayerState s, CancellationToken ct) =>
        _broadcast(new[]
        {
            "track", index.ToString(), ((int)Math.Clamp(posMs, 0, Math.Max(0, s.DurationMs))).ToString(),
            s.DurationMs.ToString(), s.Uri, s.Artist, s.Title,
        }, ct);

    // ------------------------------------------------------------------ reading the source

    private const int StallMs = 250; // no audio for this long while streaming: the source is asked why

    /// <summary>
    /// Reads PCM from a source. A source gives it as fast as it is read, so the reader sets the
    /// pace: real time when nobody listens; while the game is streaming, as fast as it comes until
    /// the game holds LeadMs of audio, so that after a start or a track change the game has its
    /// buffer at once. The game reports what it holds ("buf"), which corrects the estimate.
    /// Returns when the source closes its end.
    /// </summary>
    public async Task PumpAsync(Stream pcm, CancellationToken ct)
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
                await _source.OnStallAsync(ct);
            }
            var n = await read;
            _stalled = false;
            if (n == 0) return; // the source closed its end
            var now = clock.Elapsed.TotalMilliseconds;
            var blocked = now - before;
            var passed = now - last - (blocked > 30 ? blocked : 0); // nothing is heard while nothing comes
            aheadMs -= passed;
            budgetMs = Math.Min(budgetCap, budgetMs + passed * 1.05 - n / bytesPerMs);
            last = now;

            if (stalled) await _source.OnAudioBackAsync(ct);
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
        _reported = true;
        Interlocked.Exchange(ref _gameBufMs, Math.Max(0, ms));
    }

    // ------------------------------------------------------------------ the game going away

    /// <summary>
    /// The game stops talking while it is paused, in the main menu or loading. The source is
    /// paused then, and resumed when the game is back, so the music continues where it stopped:
    /// the game keeps the chunks it holds.
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
                    // Marked away only once the pause went through; a failed request is retried.
                    if (await _source.PauseForAwayAsync(ct)) Interlocked.Exchange(ref _awayPaused, 1);
                    _awaySinceUtc = DateTime.UtcNow;
                    _reported = false;
                    _away = true;
                }
                else if (!quiet && _away && _reported)
                {
                    // Only a buffer report counts: after a level change the new scripts say
                    // hello first and ask for the stream a few seconds later, which resumes it.
                    _away = false;
                    if (Interlocked.Exchange(ref _awayPaused, 0) == 1) await _source.ResumeAfterAwayAsync(ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Info("Game audio: " + e.Message);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    // ------------------------------------------------------------------ chunking

    private void NewSegment()
    {
        _draining = false;
        _held = false;
        _pending.SetLength(0);
        _history = Array.Empty<byte>();
        _alignPending = true;
        _resetPace = true;
        _segStartUtc = DateTime.MinValue;
        _source.OnNewSegment();
    }

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
                // The game is gone and something is playing on this source anyway. Nobody is
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
                _source.OnSegmentStarted(_index, ct);
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
        _source.EnsureReady();
        var outDir = ResolveGameDir(gameDir) ?? throw new SpotifyException($"Folder \"{gameDir}\" is not inside the game's gamedata");
        var prepared = await _source.PrepareAsync(ct);
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
            _source.OnStreamReset(prepared);
            _away = false;
            _dropped = false;
            Interlocked.Exchange(ref _gameBufMs, -1);
            _gameHolds = false;
            _active = true;
            resume = Interlocked.Exchange(ref _awayPaused, 0) == 1; // only a pause of ours is undone
        }
        finally { _gate.Release(); }

        await _source.TakeOverAsync(prepared, resume, ct);
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
            await _source.ReleaseAsync(ct);
        }
    }
}
