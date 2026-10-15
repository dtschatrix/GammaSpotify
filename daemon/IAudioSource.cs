namespace SpotifyRadio;

/// <summary>
/// Where the audio comes from, as seen by the streamer (GameAudio). A source hands its PCM
/// (44.1 kHz, 16-bit, stereo) to GameAudio.PumpAsync and is asked the things only it can know:
/// how to take playback over when the game wants the stream, what a silence in the audio means,
/// and which track a new stretch of audio belongs to.
///
/// Several sources can run at once (Spotify and YouTube); one of them is current. Only the
/// current source's audio reaches the game, and only it gets the calls marked "current only".
/// </summary>
public interface IAudioSource
{
    /// <summary>For the log: "Spotify", "YouTube".</summary>
    string Name { get; }

    /// <summary>Called once by the streamer, before anything else.</summary>
    void Attach(GameAudio audio);

    /// <summary>What the source needs to run is there.</summary>
    bool Available { get; }

    /// <summary>What is missing when it is not available, for the log.</summary>
    string Missing { get; }

    /// <summary>Runs for as long as the daemon does, feeding GameAudio.PumpAsync.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Stops whatever the source started; safe to call at any time.</summary>
    void Kill();

    // ------------------------------------------------------------------ the game asks for the stream (current only)

    /// <summary>Throws a SpotifyException saying why audio cannot be handed over right now.</summary>
    void EnsureReady();

    /// <summary>
    /// Before the stream is set up: anything that can still fail (throws SpotifyException).
    /// What it returns is handed back to the two calls below and means nothing to the streamer.
    /// </summary>
    Task<object?> PrepareAsync(CancellationToken ct);

    /// <summary>The stream has been set up. Called under the streamer's lock.</summary>
    void OnStreamReset(object? prepared);

    /// <summary>Start producing for the game. <paramref name="resume"/>: the source was paused because the game was away.</summary>
    Task TakeOverAsync(object? prepared, bool resume, CancellationToken ct);

    /// <summary>The game no longer wants the stream. Called for every source.</summary>
    Task ReleaseAsync(CancellationToken ct);

    /// <summary>Another source has become the current one: stop producing audio.</summary>
    Task DeactivateAsync(CancellationToken ct);

    // ------------------------------------------------------------------ while streaming (current only)

    /// <summary>Everything the game held was dropped; what comes next is a new stretch of audio. Called under the streamer's lock.</summary>
    void OnNewSegment();

    /// <summary>
    /// The first audio of a segment went into chunk <paramref name="index"/>. Called under the
    /// streamer's lock; GameAudio.SegmentStartSourceByte says where in the source's own output
    /// that audio was.
    /// </summary>
    Task OnSegmentStartedAsync(int index, CancellationToken ct);

    /// <summary>No audio has come for a moment while the game is streaming.</summary>
    Task OnStallAsync(CancellationToken ct);

    /// <summary>Audio is coming again after a stall.</summary>
    Task OnAudioBackAsync(CancellationToken ct);

    /// <summary>The game stopped answering. True when something was playing and has been paused for that reason.</summary>
    Task<bool> PauseForAwayAsync(CancellationToken ct);

    /// <summary>The game is back and <see cref="PauseForAwayAsync"/> had paused the source.</summary>
    Task ResumeAfterAwayAsync(CancellationToken ct);

    // ------------------------------------------------------------------ the PDA's source switch

    /// <summary>
    /// Comes just before the player switches away from this source (current only): the track the
    /// game was playing (<paramref name="uri"/>) and where in it (ms). The source reads ahead of
    /// what is heard, so this is where to carry on when it is switched back to.
    /// </summary>
    void NoteHeard(string uri, double posMs);

    /// <summary>The player switched to this source: it becomes current and carries on with what it played last.</summary>
    Task SwitchInAsync(CancellationToken ct);
}

/// <summary>What the game was playing when the player switched to another source (see <see cref="IAudioSource.NoteHeard"/>).</summary>
public sealed record Heard(string Uri, double PosMs, DateTime Utc)
{
    /// <summary>Notes are used by the switch that follows at once; an old one means that switch failed.</summary>
    public bool Fresh => DateTime.UtcNow - Utc < TimeSpan.FromSeconds(15);
}

/// <summary>What the PDA can ask of whatever is playing: the commands of the game link.</summary>
public interface IPlayer
{
    Task<PlayerState> GetStateAsync(CancellationToken ct);

    /// <summary>The regular poll. Same answer as <see cref="GetStateAsync"/>, but the player may act on what it sees.</summary>
    Task<PlayerState> PollAsync(CancellationToken ct);

    Task PlayAsync(CancellationToken ct);
    Task PauseAsync(CancellationToken ct);
    Task NextAsync(CancellationToken ct);
    Task PreviousAsync(CancellationToken ct);
    Task SetVolumeAsync(int percent, CancellationToken ct);
    Task SetShuffleAsync(bool on, CancellationToken ct);
    /// <summary>Jumps to <paramref name="positionMs"/> in the current track; a pause stays a pause.</summary>
    Task SeekAsync(int positionMs, CancellationToken ct);
    Task PlayUriAsync(string uri, CancellationToken ct);
    Task QueueAsync(string uri, CancellationToken ct);
    Task<List<SearchItem>> SearchAsync(string query, CancellationToken ct);
    Task<List<SearchItem>> MyPlaylistsAsync(CancellationToken ct);

    /// <summary>What plays after the current track, in order, up to <paramref name="max"/>.</summary>
    Task<List<SearchItem>> UpNextAsync(int max, CancellationToken ct);

    /// <summary>Takes entry <paramref name="index"/> of <see cref="UpNextAsync"/> out, if it is still <paramref name="uri"/>.</summary>
    Task RemoveUpNextAsync(int index, string uri, CancellationToken ct);
}
