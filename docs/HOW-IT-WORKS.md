# How it works

Engine behaviour described here comes from the [xray-monolith](https://github.com/themrdemonized/xray-monolith) source (`src/xrSound`, `src/xrCore`) and from testing in game.

## Parts

```
Spotify <-- Web API --> daemon <-- two text files in appdata --> game scripts
                          |
                          +-- librespot (child process, raw PCM on stdout)
                          +-- one-second .ogg files -> gamedata\levels\<level>\
```

- `spotify_radio.script`: link to the daemon, player state.
- `spotify_radio_ui.script`, `ui_spotify_radio.xml`: the PDA tab. Wraps `pda.set_active_subdialog` and returns its own window for the radio tab.
- `spotify_radio_stream.script`: plays the chunks.
- `daemon/`: Web API client (PKCE, no client secret), the game link, and `GameAudio.cs`, which runs librespot and cuts its output into chunks.

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

Refusals only happen while a chunk is waiting, so a sound starts with five chunks (three attached, two waiting). That is the five-second delay.

A chunk file is deleted once the chunk three places after it has been accepted.

## When the stream runs

The script asks for the stream (`stream start head <level folder>`) as soon as the daemon answers. Scripts restart on every level change, so it is asked for again then; chunks announced before the daemon confirms are ignored. The daemon moves Spotify playback to its librespot device without changing play / pause.

While the game does not answer (loading, paused), the daemon keeps reading audio but writes no files. After `awayPauseSeconds` more it pauses Spotify, and resumes it on the next `stream start`. Without `librespot.exe` it answers `stream unavailable` and the mod is a remote control for the Spotify app.

## Stopping instead of resuming

When the engine stops rendering a chained sound and resumes it (pause, culling, its channel taken by another sound), it rewinds to the heard position, which is usually in a file it has already released, and plays about a second of wrong audio. The script therefore:

- stops the sound when the main menu opens, and on the first update after game time stood still while real time advanced;
- stops the sound when the effects volume is near zero, before the engine culls it;
- gives the head-mode sound a large `min_distance`, which gives a 2D sound top priority so its channel is not taken.

A new sound is then started from the newest chunks.

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

`test/run_tests.sh` runs the daemon against `mock_spotify.py` (fake Web API) and `fake_librespot.py` (synthetic PCM), and runs the game scripts under LuaJIT in `harness_ui.lua`. The harness fakes the UI, the file index, and the sound streamer: three buffers of 400 or 800 ms, the two-tail limit, and a counter for attaches made after the data ran out, which must be zero. `audio_check.py` decodes the chunks the fake engine received and compares them with the source. The harness enables world mode for its run, and loads a second, unmodified copy of the stream script to check what ships.
