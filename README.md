# Spotify Radio for S.T.A.L.K.E.R. Anomaly / GAMMA

A Spotify player in the PDA's Radio tab: now playing, play / pause / next / previous, volume, shuffle, search, your playlists. The music is played by the game engine, not by the Spotify app.

Two parts: a mod, installed with Mod Organizer 2, and a daemon, a console program that runs beside the game and talks to Spotify.

Tested only on GAMMA. Not affiliated with Spotify or GSC.

## Requirements

To play:

- Anomaly 1.5.3 with the modded exes (GAMMA includes them), Mod Organizer 2, Windows
- Spotify Premium
- Your own Spotify app registration (free, see Install)

To build from source ([`docs/BUILDING.md`](docs/BUILDING.md)):

- [.NET SDK](https://dotnet.microsoft.com/download) 7 or newer
- [Rust](https://rustup.rs), plus `mingw-w64` on Linux or the Visual Studio C++ build tools on Windows
- For the tests (Linux only): LuaJIT with LuaSocket, Python 3 with NumPy, `oggdec`, `curl`

## librespot

The audio comes from [librespot](https://github.com/librespot-org/librespot), an unofficial Spotify client. Its README says using it is "probably forbidden" by Spotify. Everything else goes through Spotify's official Web API. Audio is not kept: each one-second chunk is deleted after it plays.

To do without it, delete `daemon\librespot\librespot.exe`. The PDA then only remote-controls the Spotify app, and the music comes from there.

## Install

Download the zip from Releases and unpack it.

1. Copy `mod/Spotify Radio` into MO2's `mods` folder, press F5 in MO2 and enable it.
2. At <https://developer.spotify.com/dashboard> create an app with redirect URI `http://127.0.0.1:47801/callback` and API "Web API". Copy its Client ID.
3. Run `daemon/run.bat`. Enter the Client ID and the path of your Anomaly folder (the one containing `appdata`, not the GAMMA folder), then log in to Spotify in the browser, twice: once for the daemon and once for librespot. Windows may warn that the program is unsigned.

Keep the daemon running while you play.

## Use

Open the PDA's Radio tab.

- Top right: previous, play / pause, next, volume, shuffle.
- **Search**: type, press Enter. **Play** starts a result, **Queue** queues a track.
- **My playlists** lists your playlists.

In Spotify the game appears as a device named "STALKER PDA". Playback moves to it when a save is loaded, playing or paused as it was.

## Limitations

- The game is about five seconds behind Spotify. Skips, pauses and track changes are heard five seconds late.
- Stereo only. Positional playback is in the code but disabled, because the engine's doppler bends the pitch when you move.
- Volume follows the game's SFX slider.
- The sound stops in the main menu and while paused, and resumes a few seconds after you return.
- During loading screens Spotify keeps playing unheard. If the game is away for about 90 seconds, or you quit, Spotify is paused; it resumes when the game is back.
- The game's own Radio tab is replaced.
- Titles are transliterated to Latin letters by default (see `encoding`).
- Search returns at most 10 results per type, which is Spotify's limit.
- No podcasts or local files.

## Settings

`%APPDATA%\SpotifyRadio\config.json`, or a `config.json` next to `run.bat` (see `config.example.json`).

| Key | Default | |
| --- | --- | --- |
| `clientId` | | Client ID of your Spotify app. |
| `ipcDir` | | The game's `appdata` folder. Remove it to be asked again. |
| `preferredDevice` | `""` | Part of a device name to use when no device is active. Empty: this PC. |
| `encoding` | `ascii` | `ascii` transliterates titles; `windows-1251` keeps Cyrillic if your fonts have it. |
| `searchLimit` | `10` | Results per type. |
| `streamDeviceName` | `STALKER PDA` | Name of the game's Spotify device. |
| `awayPauseSeconds` | `60` | How long after the game stops answering (about 30 s after its last message) Spotify is paused. |

Do not change `chunkMs`.

## Troubleshooting

- **"Not connected" or "Waiting for the daemon"**: the daemon is not running, or `ipcDir` is not the `appdata` of the game you are running. The daemon prints the folder it watches.
- **No sound**: check the daemon window for an error, the SFX volume, and that Spotify shows "STALKER PDA" as the playing device. Opening the Radio tab asks for the stream again.
- **"Spotify rejected the login"**: wrong Client ID or redirect URI. Delete `tokens.json` to log in again; delete `librespot-cache` to redo librespot's login.
- **Freeze or crash**: open an issue with the last 50 lines of `appdata\spotify_radio_cmd.txt` and what you were doing.

## How it works

The daemon runs librespot as a Spotify Connect device, cuts its audio into one-second OGG files and writes them into the game's folders. The game script makes the engine load each file and chains it onto the sound already playing. Details in [`docs/HOW-IT-WORKS.md`](docs/HOW-IT-WORKS.md).

## Credits

- The idea of making the game a Spotify Connect device is from [Spotify Radio for Forza Horizon](https://www.nexusmods.com/forzahorizon6/mods/95) by BigJohn ([source](https://github.com/matkhl/FH6-Spotify-Radio)). No code is shared.
- [librespot](https://github.com/librespot-org/librespot) (MIT; licence in `daemon/librespot/`)
- [OggVorbisEncoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder) (MIT)
- [xray-monolith](https://github.com/themrdemonized/xray-monolith), whose source shows how the sound engine behaves
- Written with the help of [Claude](https://claude.ai), Anthropic's AI assistant

## Licence

MIT, see [`LICENSE`](LICENSE).
