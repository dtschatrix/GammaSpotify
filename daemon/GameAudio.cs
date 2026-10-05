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
    private readonly Func<bool> _gameIsThere;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
    private DateTime _lastGameSeenUtc;
    private bool _holding;        // game not answering: audio is read but not written
    private bool _resumeOnStart;  // Spotify was paused because the game went away

    public GameAudio(Config cfg, SpotifyClient spotify, Func<string[], CancellationToken, Task> broadcast, Func<bool> gameIsThere)
    {
        _cfg = cfg;
        _spotify = spotify;
        _broadcast = broadcast;
        _gameIsThere = gameIsThere;
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

    /// <summary>
    /// Reads PCM at the speed it would be played. librespot's pipe output is as fast as the reader,
    /// so reading faster than real time would make tracks finish early.
    /// </summary>
    private async Task PumpAsync(Stream pcm, CancellationToken ct)
    {
        var buffer = new byte[FrameBytes * (SampleRate / 20)]; // 50 ms
        var clock = Stopwatch.StartNew();
        double allowedAtMs = 0;
        const double bytesPerMs = SampleRate * FrameBytes / 1000.0;
        while (true)
        {
            var n = await pcm.ReadAsync(buffer, ct);
            if (n == 0) return; // librespot exited

            await OnPcmAsync(buffer.AsMemory(0, n), ct);

            var now = clock.Elapsed.TotalMilliseconds;
            allowedAtMs = Math.Max(allowedAtMs, now) + n / bytesPerMs;
            var ahead = allowedAtMs - now;
            if (ahead > 120) await Task.Delay((int)(ahead - 100), ct);
        }
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

            if (_gameIsThere())
            {
                _lastGameSeenUtc = DateTime.UtcNow;
                if (_holding)
                {
                    _holding = false;
                    _alignPending = true;
                    _history = Array.Empty<byte>();
                }
            }
            else
            {
                // The game is loading, paused or gone. Keep Spotify playing but write nothing;
                // if it stays away, pause Spotify. The game asks for the stream again when it is back.
                if (!_holding)
                {
                    Log.Info("Game audio: the game is not answering, holding the audio back.");
                    _holding = true;
                    _pending.SetLength(0);
                }
                if (DateTime.UtcNow - _lastGameSeenUtc > TimeSpan.FromSeconds(Math.Max(1, _cfg.AwayPauseSeconds)))
                {
                    Log.Info("Game audio: the game is gone, pausing Spotify.");
                    _active = false;
                    _holding = false;
                    _resumeOnStart = true;
                    _ = Task.Run(() => PauseQuietlyAsync(), CancellationToken.None);
                }
                return;
            }

            if (_alignPending)
            {
                // a stream can start in the middle of a stereo frame: skip to the next frame boundary
                var skip = (FrameBytes - phase) % FrameBytes;
                if (skip >= data.Length) return;
                data = data[skip..];
                _alignPending = false;
            }
            _pending.Write(data.Span);

            var chunkBytes = (int)((long)SampleRate * _cfg.ChunkMs / 1000) * FrameBytes; // whole frames
            while (_pending.Length >= chunkBytes)
            {
                var all = _pending.ToArray();
                await EmitAsync(all.AsMemory(0, chunkBytes), ct);
                _history = all.AsSpan(Math.Max(0, chunkBytes - HistoryBytes), Math.Min(chunkBytes, HistoryBytes)).ToArray();
                _pending.SetLength(0);
                _pending.Write(all, chunkBytes, all.Length - chunkBytes);
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
            _lastGameSeenUtc = DateTime.UtcNow;
            _holding = false;
            if (previous is not null) _previousDeviceId = previous.Id;
            _active = true;
            resume = _resumeOnStart;
            _resumeOnStart = false;
        }
        finally { _gate.Release(); }

        // Moving playback keeps it playing or paused as it was; only a pause of ours is undone.
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
            _pending.SetLength(0);
        }
        finally { _gate.Release(); }
        if (wasActive)
        {
            Log.Info("Game audio: stream stopped.");
            await RestoreDeviceAsync(ct);
        }
    }

    private async Task PauseQuietlyAsync()
    {
        try { await _spotify.PauseAsync(CancellationToken.None); }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException)
        {
            Log.Info("Game audio: could not pause Spotify: " + e.Message);
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
