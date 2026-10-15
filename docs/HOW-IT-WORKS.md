# How it works

Engine behaviour described here comes from the [xray-monolith](https://github.com/themrdemonized/xray-monolith) source (`src/xrSound`, `src/xrCore`) and from testing in game.

## Parts

```
Spotify <-- Web API --> daemon <-- two text files in appdata --> game scripts
                          |
                          +-- librespot (child process, raw PCM on stdout)
                          +-- yt-dlp (child process from yt\, finds a track's audio stream)
                          +-- ffmpeg (child process from yt\, decodes it to raw PCM on stdout)
                          +-- one-second .ogg files -> gamedata\levels\<level>\
```

- `spotify_radio.script`: link to the daemon, player state.
- `spotify_radio_ui.script`, `ui_spotify_radio.xml`: the PDA tab. Wraps `pda.set_active_subdialog` and returns its own window for the radio tab.
- `spotify_radio_stream.script`: plays the chunks.
- `daemon/`: Web API client (PKCE, no client secret), the game link (`GameServer.cs`), `SpotifySource.cs`, which runs librespot and follows what Spotify is doing, `YouTubeSource.cs`, which runs yt-dlp and ffmpeg and keeps its own queue, and `GameAudio.cs`, which cuts the audio it is handed into chunks and paces it for the game. `GameAudio.cs` takes several sources through `IAudioSource.cs`; one of them is current.

## Game <-> daemon

LuaSocket only loads with `lua_debug 1`, so the link is two append-only text files in `appdata`: `spotify_radio_cmd.txt` (written by the game) and `spotify_radio_out.txt` (written by the daemon). Each side tracks its read offset. One message per line; replies are tab-separated. The game pings every 4 s. A TCP link on port 47800 also exists (`PREFER_SOCKET`).

The game sends text as Windows-1251. The daemon sends ASCII by default.

The stream script writes `log ...` lines into `spotify_radio_cmd.txt`. The game's own log is buffered and loses its tail on a hang.

## Loading a file into a running game

The engine indexes files at start and does not rescan.

1. The daemon writes the chunk into the current level folder (`$level$`) under a temporary name, then renames it.
2. The script calls `getFS():rescan_path("$level$")` and `rescan_pathes()`. This path is small and non-recursive, about 1 ms. Rescanning `gamedata\sounds` under MO2 stalls the game.
3. The sound loader cannot load from `$level$` (it returns a silent placeholder), so the script `file_rename`s the file into `gamedata\sounds\spotify_radio\`. That moves the file and its index entry. Under MO2 it ends up in `overwrite`.

From Lua, use only FS calls that return nothing or plain values. `append_path` and `get_path` return a type luabind does not know and put the game into "unstable state", even inside `pcall`.

Files must be 44.1 kHz; mono for 3D sound.

## Chaining chunks

Separate sounds cannot be joined seamlessly because starts are quantised to frames. One sound is started and each further chunk is added with `sound_object:attach_tail(name)`; the engine streams from one file into the next.

Constraints:

- At most two tails can be queued. A third `attach_tail` is ignored; the only sign is that `length()` does not grow.
- The engine reads ahead in three buffers. A buffer is 400 ms, or 800 ms: its size is a global set by the last sound started, and a stereo sound doubles it. So up to 2.4 s is read when a sound starts, and the read position then moves in steps of up to 0.8 s.
- If the read position reaches the end of the data and a tail is attached afterwards, `CSoundRender_Emitter::fill_block` recurses forever and the game freezes.
- For a 3D sound, doppler changes the playback rate while the listener moves, so the read position cannot be derived from elapsed time alone.

The script keeps an upper bound on the read position:

- 2.4 s when a sound starts;
- on every refused `attach_tail`: the start of the last two chunks plus 0.8 s, since a refusal means both tails are still queued;
- otherwise it grows with elapsed time times a rate above 1.

A chunk is offered every update and attached only while the bound is at least 300 ms short of the end. If not, the sound is left to run out and a new one starts from the newest chunks.

Refusals only happen while a chunk is waiting, so a sound starts with five chunks (three attached, two waiting).

A chunk file is deleted 1.5 s after it has been heard to its end, and never while it is one of the last three the engine was given.

## Latency

Five chunks at one per second would mean five seconds before anything is heard. librespot's pipe output is as fast as its reader, so the daemon reads ahead of real time until the game holds `leadMs` (6 s) of audio, then keeps it there. After a start or a track change the five chunks arrive within a moment.

- Once a second the game reports how much it holds and the last chunk it received (`buf <ms> <index>`). The daemon paces its reading on that. When the game holds too much the daemon slows to half speed rather than stopping: librespot handles pause and skip only between writes. Reading ahead is also capped by a budget that does not depend on the reports.
- Pause: the game stops the sound and puts the chunks not heard yet back in its queue; play starts a sound from them. A chunk is only played from its start, so the one being heard is kept or dropped, whichever is nearer.
- Track change: the game drops everything it holds. The daemon discards audio until the gap that precedes the new track, then starts a new segment (`audio flush`).
- The PDA tells the stream script what was pressed before the command goes to Spotify, so the sound reacts in the same frame. For pauses, skips and seeks made elsewhere the daemon notices the audio stopping (no data for 250 ms), asks Spotify why, and sends `audio hold`, `audio resume` or `audio flush`.
- The PDA's volume buttons set the volume of the sound object. The daemon stores the value (`gamevol`) and returns it with `stream started`.

## What the PDA shows

What is heard is seconds behind Spotify, so Spotify's own position is no use for the display. Spotify's position is "where the player last reported plus the time since", at whatever speed the audio is read. The audio read at the moment of a report is therefore at the reported position:

- After a start or flush, the first audio of the segment is at `progress - (time since it was read)`.
- When the next track starts without a break, its first audio was read `progress` ago; the daemon keeps the read time of each chunk and finds the one.

The daemon sends `track <chunk> <ms> <duration> <uri> <artist> <title>`: from that chunk on it is this track, and the chunk starts at that position. The PDA shows the track and time of the chunk being played.

## When the stream runs

The script asks for the stream (`stream start head <level folder>`) three seconds after the level has loaded, once the daemon answers. Scripts restart on every level change, so it is asked for again then; chunks announced before the daemon confirms are ignored. The daemon moves Spotify playback to its librespot device without changing play / pause.

Scripts do not run while the game is paused, in the main menu or loading. When the game has said nothing for `awaySeconds` the daemon pauses Spotify, and resumes it at the game's next buffer report or `stream start`. The game keeps its queue meanwhile, so nothing is lost. If audio flows for ten seconds while the game is away (someone pressed play elsewhere), the daemon stops the stream and sends `stream ended` when the game is back, and the game asks again.

Without `librespot.exe` the daemon answers `stream unavailable` and the mod is a remote control for the Spotify app.

## YouTube

The PDA radio tab has a switch between Spotify and YouTube Music when the daemon has both and both can play in the game. It is a button next to the title, "Switch to YouTube" or "Switch to Spotify". The title shows SPOTIFY (green) or YOUTUBE MUSIC (red). With only one source the switch is hidden.

Search goes to the source the switch is on, which is always the source the game hears. The search box caption says "Search Spotify" or "Search YouTube Music". "My playlists" is hidden on YouTube. A search that starts with `yt ` searches YouTube Music whichever source is on. Results are songs, albums and playlists (URIs `yt:video:<id>`, `yt:album:<browseId>`, `yt:playlist:<id>`). Playing a result of the other source (a Spotify result from an earlier search, say) switches to that source too. The transport buttons (play, pause, next, previous, shuffle) act on whichever source is current.

Search is a direct request to YouTube Music's own search endpoint (`music.youtube.com/youtubei/v1/search`, client `WEB_REMIX`), because yt-dlp's search returns titles without artist or length.

yt-dlp looks up a track's stream, asking only the `visionos` YouTube client first and using yt-dlp's defaults as fallback. ffmpeg decodes the stream to 44.1 kHz 16-bit stereo, the same PCM librespot gives. One ffmpeg runs per track.

Nothing outside runs the queue, so the daemon is the player: a list, next / previous, shuffle, and queued tracks that play after the current one. When the list runs out, the daemon adds YouTube's mix of the last track (the `RD<videoId>` list, which YouTube Music plays as a radio), leaving out what was already played, and plays on without a break: the game still holds several seconds of audio while the next stream is looked up.

While paused, ffmpeg's output is not read. If a stream ends early (the connection dropped after a long pause, or the address was refused; a 403 on a fresh address happens), ffmpeg is started again at that position, with a fresh lookup when no audio came at all. The position is given with `-ss` after `-i`; before `-i` the Windows ffmpeg build gets HTTP 400 from YouTube.

Track marks are exact: the daemon decodes the audio itself, so it knows at which byte of its output each track starts. When the player changes track, everything YouTube handed out before that byte is dropped, even if it is still on its way to the chunker.

YouTube answers "confirm you're not a bot" (or 429) after about 20-25 lookups in a few minutes from one address. Only the current track and the next one are looked up, addresses are kept until they expire (about six hours), and that answer stops all lookups for ten minutes instead of being retried.

Switching source drops what the game holds and carries on with what the other source played last, from what the game was playing when it was left. The game holds several seconds the source has already read, so the source itself is ahead of what was heard. The game sends `source spotify|youtube [URI MS]`, where URI and MS are the track and position being heard on the source it leaves.

- Spotify is paused when left. Switched back to, it seeks to that position (if Spotify is still on that track) and plays. While YouTube plays, librespot's output is still read at real time and dropped.
- YouTube's track is parked when left: its ffmpeg is left running but unread, and the last 16 s it read are kept. Switched back to, those seconds from the heard position are handed out again and then the pipe is read on, so it continues at once.
- If nothing was playing on the source, the game goes quiet until something is chosen.
- Without a stream (the game is not asking for music), the switch only changes which source the buttons and search control. YouTube's parked track then plays on when play is pressed.

`config.json` has `"sources"`: `["spotify"]`, `["youtube"]` or both. On the first run, or with a config from before this existed, the daemon asks "What should play in the game?": 1 Spotify (needs Spotify Premium), 2 YouTube Music (free), 3 both. With an existing Spotify setup, Enter keeps Spotify. Removing `"sources"` makes it ask again. With YouTube only, the daemon asks no Spotify questions (no app registration, no Client ID, no login) and starts no librespot. A Spotify address then gets the error "Spotify is not set up in this daemon". `"lastSource"` holds where the PDA switch was left; the daemon starts on that source next time if it can play.

When YouTube is chosen and yt-dlp or ffmpeg is missing, the daemon offers to download them (about 95 MB download, 190 MB on disk) from their own GitHub releases. yt-dlp is the unpacked Windows build `yt-dlp_win.zip` from github.com/yt-dlp/yt-dlp, going into `yt\yt-dlp_win\`. ffmpeg is the newest stable `win64-lgpl-shared` build from github.com/BtbN/FFmpeg-Builds, going into `yt\ffmpeg\`: only `ffmpeg.exe`, the DLLs it loads and the licence as `LICENSE-ffmpeg.txt`. Each download is checked against the SHA-256 list published in the same release (`SHA2-256SUMS`, `checksums.sha256`); a mismatch is refused and leaves the installed copy as it was. Nothing from these projects ships with the mod. If the offer is declined, the daemon runs without YouTube when Spotify is also chosen, and with YouTube only it stops and says why.

`--install-youtube` downloads whatever is missing; `--update-youtube` downloads the latest yt-dlp again, since YouTube breaks old versions every few weeks. From source: `dotnet run -c Release -- --install-youtube`. A single `yt\ffmpeg.exe` or `yt\yt-dlp.exe` put in by hand still works; the unpacked yt-dlp build starts about half a second faster per lookup.

## Stopping instead of resuming

When the engine stops rendering a chained sound and resumes it (pause, culling, its channel taken by another sound), it rewinds to the heard position, which is usually in a file it has already released, and plays about a second of wrong audio. The script therefore:

- stops the sound when the main menu opens, and on the first update after game time stood still while real time advanced;
- stops the sound when the volume is near zero, before the engine culls it;
- gives the head-mode sound a large `min_distance`, which gives a 2D sound top priority so its channel is not taken.

A new sound is then started from the chunks that were not heard (after a pause) or from the newest ones (after being inaudible).

## Encoder

Chunks are encoded with the `OggVorbisEncoder` NuGet package. Checked by decoding its output with libvorbis:

- It drops the first 1024 samples. The daemon prepends 1024 samples of the preceding audio and verifies the final granule equals the chunk length.
- Output is corrupt at most quality settings. Mono 0.5, 0.8, 1.0 and stereo 0.5 are good. The daemon uses mono 0.8 and stereo 0.5.

## World mode (disabled)

`spotify_radio_stream.script` contains a positional mode, switched off by `WORLD_ENABLED`. The sound plays from a fixed point with distance falloff applied through its volume. Open problems:

- Doppler bends the pitch when the player moves. There is no per-sound switch; `snd_doppler_power 0` disables it globally.
- The sound is created as "world ambient" with a 1000 m engine range so the engine never culls it. As a result walls do not muffle it.
- The point is moved to the player whenever something is started from the PDA. There is no world object.

## Tests

`test/run_tests.sh` runs the daemon against `mock_spotify.py` (fake Web API) and `fake_librespot.py`, and runs the game scripts under LuaJIT in `harness_ui.lua`. The mock keeps a position the way Spotify does; the fake librespot follows its play / pause / track state, leaves a gap when a track loads, and plays track N at position P from a known place in the test audio. The harness fakes the UI, the file index, and the sound streamer: three buffers of 400 or 800 ms, the two-tail limit, and a counter for attaches made after the data ran out, which must be zero. `audio_check.py` decodes the chunks the fake engine received and compares them with the source. `heard_check.py` compares the track and time the PDA showed with the audio the fake engine was playing at that moment. The harness enables world mode for part of its run, then loads unmodified copies of the stream script to check what ships. `QUICK=1` skips the world-mode part.

The YouTube part uses `fake_ytdlp.sh` in place of yt-dlp and the real ffmpeg, decoding WAVs cut from the test audio at known places, so `heard_check.py` checks YouTube samples too. `fake_ytdlp.sh` answers after 1.2 s like a real lookup; some ids fail or give a refused address on purpose. The mock server also answers YouTube Music's search.

`run_tests.sh` also runs `setup_check.py` (first-run questions and the download, against fake GitHub releases served locally) and `youtube_only.py` (a daemon set up for YouTube only, driven over the file link like the game does).
