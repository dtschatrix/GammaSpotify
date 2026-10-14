using System.Diagnostics;

namespace SpotifyRadio;

/// <summary>
/// Spotify as the audio source, and as the player the PDA controls.
///
/// librespot (an open-source Spotify Connect client) runs as a child process and shows up in
/// Spotify as its own device. Its audio comes out of its stdout as raw PCM instead of going to
/// the speakers, and is handed to GameAudio.
///
/// Spotify's servers run the queue; this class only follows. It cannot see a pause or a skip
/// directly: both show up as the audio stopping, and the Web API is asked which it was. The same
/// goes for which track a stretch of audio is.
/// </summary>
public sealed class SpotifySource : IAudioSource, IPlayer
{
    private const int DrainMs = 2500; // after a change was asked for, the old track is dropped this long at most

    private readonly Config _cfg;
    private readonly SpotifyClient _spotify;
    private readonly SemaphoreSlim _reactGate = new(1, 1);
    private GameAudio? _audio;
    private Process? _process;

    private string? _previousDeviceId;
    private PlayerState? _lastState;   // last Spotify state seen, and when
    private DateTime _lastStateUtc;
    private volatile bool _needMark;   // the game has not been told what this segment is
    private string _markUri = "";      // track the newest chunks belong to

    public SpotifySource(Config cfg, SpotifyClient spotify)
    {
        _cfg = cfg;
        _spotify = spotify;
    }

    public string Name => "Spotify";

    public void Attach(GameAudio audio) => _audio = audio;

    /// <summary>Spotify is the source the game hears (YouTube may be instead).</summary>
    private bool IsCurrent => _audio is { } a && a.IsCurrent(this);

    private GameAudio Audio => _audio ?? throw new InvalidOperationException("the audio source is not attached to the game audio");

    private string ExePath => !string.IsNullOrWhiteSpace(_cfg.LibrespotPath)
        ? _cfg.LibrespotPath
        : Path.Combine(Config.AppDir, "librespot", OperatingSystem.IsWindows() ? "librespot.exe" : "librespot");

    private string CacheDir => Path.Combine(Config.HomeDir, "librespot-cache");

    public bool Available => File.Exists(ExePath);
    public string Missing => $"{ExePath} not found";

    // ------------------------------------------------------------------ librespot process

    /// <summary>Keeps librespot running for as long as the daemon runs.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
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
            await Audio.PumpAsync(this, process.StandardOutput.BaseStream, ct);
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

    // ------------------------------------------------------------------ the game asks for the stream

    public void EnsureReady()
    {
        if (!Available) throw new SpotifyException("Game audio needs librespot.exe in the daemon's librespot folder");
        if (_process is null or { HasExited: true }) throw new SpotifyException("Game audio is not running yet - check the daemon window");
    }

    /// <summary>The game's Spotify device, and the one that is playing now.</summary>
    private sealed record Takeover(DeviceInfo Ours, DeviceInfo? Previous);

    /// <summary>Finds the game's Spotify device, and the one playing now.</summary>
    public async Task<object?> PrepareAsync(CancellationToken ct)
    {
        DeviceInfo? ours = null;
        for (var attempt = 0; attempt < 6 && ours is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(1000, ct);
            ours = (await _spotify.GetDevicesAsync(ct)).FirstOrDefault(d => d.Name == _cfg.StreamDeviceName);
        }
        if (ours is null)
            throw new SpotifyException($"Spotify does not list \"{_cfg.StreamDeviceName}\" yet - finish the librespot login in the browser");

        var devices = await _spotify.GetDevicesAsync(ct);
        return new Takeover(ours, devices.FirstOrDefault(d => d.Active && d.Id != ours.Id));
    }

    public void OnStreamReset(object? prepared)
    {
        _lastState = null;
        if (prepared is Takeover { Previous: { } previous }) _previousDeviceId = previous.Id;
    }

    /// <summary>Moves playback onto the game's device. Moving it keeps it playing or paused as it was.</summary>
    public async Task TakeOverAsync(object? prepared, bool resume, CancellationToken ct)
    {
        if (prepared is not Takeover { Ours: var ours }) return;
        if (!ours.Active) await _spotify.TransferAsync(ours.Id, play: resume, ct);
        else if (resume) await _spotify.PlayAsync(ct);
    }

    /// <summary>Hands playback back to the device that had it before the stream started.</summary>
    public async Task ReleaseAsync(CancellationToken ct)
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

    /// <summary>YouTube is playing now: Spotify is paused, and librespot's audio is read and dropped.</summary>
    public async Task DeactivateAsync(CancellationToken ct)
    {
        if ((await _spotify.GetStateAsync(ct)).Status != "playing") return;
        Log.Info("Game audio: pausing Spotify.");
        await _spotify.PauseAsync(ct);
    }

    /// <summary>
    /// Something is asked of Spotify while another source plays: Spotify becomes the source the
    /// game hears again, with playback on the game's device. True when it was not current before.
    /// </summary>
    private async Task<bool> BecomeCurrentAsync(CancellationToken ct)
    {
        if (_audio is not { } audio || audio.IsCurrent(this)) return false;
        if (!audio.Streaming)
        {
            await audio.StartOverAsync(this, "back to Spotify", 0, ct);
            return true;
        }
        EnsureReady();
        var prepared = await PrepareAsync(ct);
        await audio.StartOverAsync(this, "back to Spotify", 0, ct);
        using (await audio.LockAsync(ct))
        {
            _lastState = null;
            if (_previousDeviceId is null && prepared is Takeover { Previous: { } previous }) _previousDeviceId = previous.Id;
        }
        await TakeOverAsync(prepared, resume: false, ct);
        return true;
    }

    // ------------------------------------------------------------------ the game going away

    public async Task<bool> PauseForAwayAsync(CancellationToken ct)
    {
        if ((await _spotify.GetStateAsync(ct)).Status != "playing") return false;
        Log.Info("Game audio: the game is not answering, pausing Spotify.");
        await _spotify.PauseAsync(ct);
        return true;
    }

    public async Task ResumeAfterAwayAsync(CancellationToken ct)
    {
        Log.Info("Game audio: the game is back, resuming Spotify.");
        await _spotify.PlayAsync(ct);
    }

    // ------------------------------------------------------------------ pauses and track changes

    /// <summary>
    /// The game holds several seconds of audio. When Spotify is paused it must stop at once and
    /// keep them; when the track is changed by hand it must drop them. Both show up here as the
    /// audio stopping, so Spotify is asked which it is.
    /// </summary>
    public async Task OnStallAsync(CancellationToken ct)
    {
        if (Audio.Draining)
        {
            await Audio.FlushAsync("new track", ct); // the gap before the new track: everything after it is new
            return;
        }
        if (Audio.Away) return;
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
    public async Task OnAudioBackAsync(CancellationToken ct)
    {
        if (Audio.Draining)
        {
            await Audio.FlushAsync("new track", ct); // a change was asked for while paused: this is the new track
            return;
        }
        if (!Audio.Held)
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
        Audio.Held = false;
        PlayerState? state = null;
        try { state = await _spotify.GetStateAsync(ct); }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException) { }

        if (state is { Status: "playing" } s && !_needMark && (OtherTrack(s) && !NearEnd() || Jumped(s)))
            await Audio.FlushAsync("track changed", ct);
        else
            await Audio.SendResumeAsync(ct);
        if (state is not null) Remember(state);
    }

    /// <summary>Called with every state the daemon polls, and when the audio stops.</summary>
    private async Task OnPolledStateAsync(PlayerState state, CancellationToken ct)
    {
        if (IsCurrent && _audio is { Streaming: true, Draining: false }) await ReactAsync(state, ct);
        else Remember(state);
    }

    private async Task ReactAsync(PlayerState state, CancellationToken ct)
    {
        // one at a time: the poll, a gap in the audio and a new segment can all ask at once
        await _reactGate.WaitAsync(ct);
        try
        {
            if (IsCurrent) await ReactOnceAsync(state, ct);
            else Remember(state);                // another source plays: nothing Spotify does is heard
        }
        finally { _reactGate.Release(); }
    }

    private async Task ReactOnceAsync(PlayerState state, CancellationToken ct)
    {
        var audio = Audio;
        if (state.Status != "playing")
        {
            // At the end of a track with nothing after it the audio stops too, but then the game
            // should play out what it holds.
            if (audio.Stalled && !audio.Held && !audio.Away && !NearEnd())
            {
                audio.Held = true;
                Log.Info("Game audio: Spotify is paused.");
                await audio.SendHoldAsync(ct);
            }
        }
        else if (_needMark) await MarkSegmentAsync(state, ct);
        else if (OtherTrack(state))
        {
            if (NearEnd()) await MarkNextTrackAsync(state, ct);
            else if ((DateTime.UtcNow - audio.SegmentStartUtc).TotalSeconds < 6)
            {
                _needMark = true;                // the first answer after a change still named the old track
                await MarkSegmentAsync(state, ct);
            }
            else await audio.FlushAsync("track changed", ct);
        }
        else if (Jumped(state)) await audio.FlushAsync("position changed", ct);
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
    private void ExpectChange()
    {
        if (_audio is not { Streaming: true } audio) return;
        audio.BeginDrain(DrainMs);
        _lastState = null;
    }

    // ------------------------------------------------------------------ which track a chunk belongs to

    // Spotify's position is "where it was last reported plus the time since", whatever speed the
    // audio is read at. So the audio read at the moment of a report is at the reported position,
    // and that ties chunks to positions: the game is told "from chunk N on it is this track, and
    // chunk N starts at P ms" and shows the track and time of the chunk it is playing.

    public void OnNewSegment()
    {
        _needMark = true;
        _markUri = "";
    }

    /// <summary>Asks Spotify what a new segment is, shortly after its first audio.</summary>
    public Task OnSegmentStartedAsync(int index, CancellationToken ct)
    {
        _ = Task.Run(() => ResolveMarkAsync(index, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>After a start or a flush: the first audio of the segment was read when playback (re)started.</summary>
    private async Task MarkSegmentAsync(PlayerState state, CancellationToken ct)
    {
        var audio = Audio;
        int index;
        double pos;
        using (await audio.LockAsync(ct))
        {
            if (!audio.Streaming || !audio.IsCurrent(this) || !_needMark || audio.SegmentStartUtc == DateTime.MinValue || state.Status != "playing" || state.Uri.Length == 0) return;
            _needMark = false;
            _markUri = state.Uri;
            index = audio.SegmentStartIndex;
            pos = state.ProgressMs - (DateTime.UtcNow - audio.SegmentStartUtc).TotalMilliseconds;
        }
        await audio.MarkAsync(index, pos, state, ct);
    }

    /// <summary>The next track began without a break: its first audio was read ProgressMs ago.</summary>
    private async Task MarkNextTrackAsync(PlayerState state, CancellationToken ct)
    {
        var audio = Audio;
        int index;
        double pos;
        using (await audio.LockAsync(ct))
        {
            if (!audio.Streaming || !audio.IsCurrent(this)) return;
            (index, pos) = audio.ChunkReadFrom(DateTime.UtcNow.AddMilliseconds(-state.ProgressMs));
            _markUri = state.Uri;
        }
        Log.Info("Game audio: next track.");
        await audio.MarkAsync(index, pos, state, ct);
    }

    private async Task ResolveMarkAsync(int segIndex, CancellationToken ct)
    {
        try
        {
            foreach (var wait in new[] { 400, 800, 1500 })
            {
                await Task.Delay(wait, ct);
                if (!Audio.Streaming || !_needMark || Audio.SegmentStartIndex != segIndex) return;
                await ReactAsync(await _spotify.GetStateAsync(ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is SpotifyException or HttpRequestException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ the player the PDA controls

    public Task<PlayerState> GetStateAsync(CancellationToken ct) => _spotify.GetStateAsync(ct);

    public async Task<PlayerState> PollAsync(CancellationToken ct)
    {
        var state = await _spotify.GetStateAsync(ct);
        await OnPolledStateAsync(state, ct);
        return state;
    }

    public Task PlayAsync(CancellationToken ct) => _spotify.PlayAsync(ct);
    public Task PauseAsync(CancellationToken ct) => _spotify.PauseAsync(ct);

    public Task NextAsync(CancellationToken ct)
    {
        ExpectChange();
        return _spotify.NextAsync(ct);
    }

    public Task PreviousAsync(CancellationToken ct)
    {
        ExpectChange();
        return _spotify.PreviousAsync(ct);
    }

    public Task SetVolumeAsync(int percent, CancellationToken ct) => _spotify.SetVolumeAsync(percent, ct);
    public Task SetShuffleAsync(bool on, CancellationToken ct) => _spotify.SetShuffleAsync(on, ct);

    /// <summary>The PDA asked for a Spotify track or list: Spotify becomes what the game hears, if it was not.</summary>
    public async Task PlayUriAsync(string uri, CancellationToken ct)
    {
        // Spotify was paused while the other source played, so no old audio is on its way:
        // nothing to wait out, and its pause would not even show as a gap.
        if (!await BecomeCurrentAsync(ct)) ExpectChange();
        await _spotify.PlayUriAsync(uri, ct);
    }

    public Task QueueAsync(string uri, CancellationToken ct) => _spotify.QueueAsync(uri, ct);
    public Task<List<SearchItem>> SearchAsync(string query, CancellationToken ct) => _spotify.SearchAsync(query, ct);
    public Task<List<SearchItem>> MyPlaylistsAsync(CancellationToken ct) => _spotify.MyPlaylistsAsync(ct);
}
