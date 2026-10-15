# Spotify Radio for S.T.A.L.K.E.R. Anomaly / GAMMA

A music player in the PDA's Radio tab, for Spotify or YouTube Music, or both with a switch between them: now playing, play / pause / next / previous, seek, volume, shuffle, search, your playlists (Spotify), what plays next. The music is played by the game engine, not by the Spotify app.

Two parts: a mod, installed with Mod Organizer 2, and a daemon, a console program that runs beside the game and talks to Spotify and YouTube Music.

Tested only on GAMMA. Not affiliated with Spotify, YouTube or GSC.

## Requirements

To play:

- Anomaly 1.5.3 with the modded exes (GAMMA includes them), Mod Organizer 2, Windows
- For Spotify: Spotify Premium and your own Spotify app registration (free, see Install)
- For YouTube Music: nothing to sign up for. The daemon downloads yt-dlp and ffmpeg on first use, about 95 MB to download and 190 MB on disk (see YouTube Music)

To build from source ([`docs/BUILDING.md`](docs/BUILDING.md)):

- [.NET SDK](https://dotnet.microsoft.com/download) 7 or newer
- [Rust](https://rustup.rs), plus `mingw-w64` on Linux or the Visual Studio C++ build tools on Windows
- For the tests (Linux only): LuaJIT with LuaSocket, Python 3 with NumPy, `oggdec`, `curl`

## librespot

The Spotify audio comes from [librespot](https://github.com/librespot-org/librespot), an unofficial Spotify client. Its README says using it is "probably forbidden" by Spotify. Everything else goes through Spotify's official Web API. Audio is not kept: each one-second chunk is deleted after it plays.

To do without it, delete `daemon\librespot\librespot.exe`. The PDA then only remote-controls the Spotify app, and the music comes from there.

## YouTube Music

The audio comes from two programs. [yt-dlp](https://github.com/yt-dlp/yt-dlp) finds a track's audio stream and ffmpeg decodes it. Using them this way is against YouTube's terms of service, as using librespot is against Spotify's. Search goes to YouTube Music's own search. No YouTube account is used.

The daemon downloads yt-dlp and ffmpeg ([BtbN's LGPL build](https://github.com/BtbN/FFmpeg-Builds)) on first use into its `yt` folder, each checked against the SHA-256 list its release publishes. Neither ships in the release zips.

YouTube blocks connections it takes for bots: many requests in a short time, and VPN exit addresses very often. The PDA then says "YouTube: blocked for this connection (a VPN? turn it off)" and the daemon waits 10 minutes before asking again. Restarting `run.bat` asks at once.

YouTube breaks old yt-dlp versions every few weeks. `SpotifyRadio.exe --update-youtube` in the daemon folder gets the latest; `--install-youtube` gets whatever is missing.

## Install

A release has two zips.

1. **The mod:** in MO2, "Install a new mod from archive" (the CD icon) and pick `Spotify-Radio-x.y-mod.zip`. Enable it. If MO2 shows a red X next to the mod, its `gamedata` folder is not at the top of the mod folder.
2. **The daemon:** unpack `Spotify-Radio-x.y-daemon.zip` anywhere outside MO2's folders, for example `C:\Games\Spotify-Radio-daemon`.
3. Only for Spotify: at <https://developer.spotify.com/dashboard> create an app with redirect URI `http://127.0.0.1:47801/callback` and API "Web API". Copy its Client ID.
4. Run `run.bat` in the daemon folder. It first asks "What should play in the game?": 1 Spotify, 2 YouTube Music, 3 both. For Spotify it asks for the Client ID, then for two browser logins, one for the daemon and one for librespot. For YouTube Music it offers to download yt-dlp and ffmpeg ([Y/n]). Last it asks for the path of your Anomaly folder (the one containing `appdata`, not the GAMMA folder). Windows may warn that the program is unsigned.

Keep the daemon running while you play. When a save is loaded the daemon window shows `game: PDA hook installed`; if it never does, see Troubleshooting.

To upgrade from 0.2, install the mod zip over the old mod (MO2 asks to replace: choose Replace) and unpack the daemon zip over the old daemon folder. The daemon asks the source question once: Enter keeps Spotify, 3 adds YouTube Music. Restart the game completely after updating the mod, as it reads its texture list at startup.

## Use

Open the PDA's Radio tab. A header shows the source. When both sources are set up, a button switches between them ("SWITCH: YOUTUBE MUSIC" or "SWITCH: SPOTIFY"). Switching stops the music and empties the list; the other source waits where you left it, and play carries on from there.

- Now playing shows the state, track, time and a seek slider (drag and let go), then volume, shuffle, device and a status line.
- Buttons: previous, play / pause, next, VOL -, VOL +, shuffle, search, my playlists (Spotify only). Volume is that of the music in game; Spotify's own volume is left alone.
- Search: type, press Enter. It goes to the source that is on; a search starting with `yt ` searches YouTube Music anyway. Results have Play, and Queue for tracks. A Spotify track plays with the tracks after it in the list, so next goes down the list.
- Up next, on the right, lists what plays after the current track. On YouTube each entry has an X that takes it out.
- On YouTube, when a track, album or playlist runs out, a radio of the last track plays on.

In Spotify the game appears as a device named "STALKER PDA". Playback moves to it when a save is loaded, playing or paused as it was.

## Limitations

Struck through means fixed; what it does now follows.

- ~~The game is about five seconds behind Spotify. Skips, pauses and track changes are heard five seconds late.~~ A new track is heard a second or two after you pick it. Pause, play and volume act at once.
- ~~The sound stops in the main menu and while paused, and resumes a few seconds after you return.~~ The music pauses with the game (main menu, pause, quitting) and continues where it stopped.
- ~~During loading screens Spotify keeps playing unheard.~~ Spotify is paused while a level loads. The music starts three seconds after the level has loaded, a few seconds further into the track.
- The game holds about six seconds of audio, so it is that far behind Spotify. The PDA shows the track and time you hear; the Spotify app is ahead. A pause or skip made in the Spotify app takes up to a second to reach the game.
- Stereo only. Positional playback is in the code but disabled, because the engine's doppler bends the pitch when you move.
- Volume also follows the game's SFX slider.
- The game's own Radio tab is replaced.
- Titles are transliterated to Latin letters by default (see `encoding`).
- Spotify search returns at most 10 results per type, Spotify's own limit.
- Up next is read-only on Spotify: the Web API cannot remove a track from Spotify's queue. On YouTube it can be edited.
- No podcasts or local files.
- On YouTube a track starts about two seconds after you pick it; finding the stream takes a second or so.
- YouTube plays only through the game; there is no remote-control mode.
- Spotify started from the phone while YouTube is on is not heard.
- After a long time on Spotify, YouTube can take a moment to come back, as its stream is opened again.
- YouTube's search, lookups and blocks are YouTube's and can change at any time.

## To do

Planned:

- More controls.
- Scrolling lists instead of pages; opening a playlist to see its tracks.
- "Start radio from this song" (YouTube only: Spotify's Web API no longer offers recommendations to new apps).

Ideas, not decided:

- Positional playback: a radio standing in the world, once doppler is dealt with.
- Repeat modes; saving the current track to your library.
- Choosing the Spotify device from the PDA.
- Settings in MCM instead of editing files.
- Starting the daemon with the game.
- Non-Latin titles by default.
- A shorter delay on track changes.
- Testing on plain Anomaly.

## Settings

`%APPDATA%\SpotifyRadio\config.json`, or a `config.json` next to `run.bat` (see `config.example.json`).

| Key | Default | |
| --- | --- | --- |
| `clientId` | | Client ID of your Spotify app. |
| `ipcDir` | | The game's `appdata` folder. Remove it to be asked again. |
| `sources` | | The sources in use, a list such as `["spotify","youtube"]`. Remove it to be asked again. |
| `lastSource` | | Where the switch was left. The daemon starts there. |
| `preferredDevice` | `""` | Part of a device name to use when no device is active. Empty: this PC. |
| `encoding` | `ascii` | `ascii` transliterates titles; `windows-1251` keeps Cyrillic if your fonts have it. |
| `searchLimit` | `10` | Results per type. |
| `streamDeviceName` | `STALKER PDA` | Name of the game's Spotify device. |
| `awaySeconds` | `3` | Spotify is paused when the game has been silent this long (paused, menu, loading, closed). |
| `leadMs` | `6000` | How much audio the game is kept supplied with. |

Do not change `chunkMs`.

## Troubleshooting

- **The PDA shows the old radio tab, and the daemon window never says "Game connected"**: the mod is not loaded. In MO2 a red X next to it means `gamedata` is not at the top of the mod folder: install the mod zip with "Install a new mod from archive", or move `gamedata` up by hand.
- **The PDA shows the old radio tab, but the daemon says "Game connected"**: another PDA mod took the radio tab. The daemon window says which way (`PDA hook ...` lines). Open an issue with the name of that mod.
- **The tab shows OFFLINE and "-- radio daemon not running --"**: the daemon is not running, or `ipcDir` is not the `appdata` of the game you are running. The daemon prints the folder it watches.
- **No sound**: check the daemon window for an error, the SFX volume, and, on Spotify, that the app shows "STALKER PDA" as the playing device. Opening the Radio tab asks for the stream again.
- **YouTube does not play**: "blocked for this connection" means YouTube took the connection for a bot; turn the VPN off, or wait. If tracks never start, or "Requested format is not available" shows up, run `SpotifyRadio.exe --update-youtube`.
- **"Spotify rejected the login"**: wrong Client ID or redirect URI. Delete `tokens.json` to log in again; delete `librespot-cache` to redo librespot's login.
- **Freeze or crash**: open an issue with the last 50 lines of `appdata\spotify_radio_cmd.txt` and what you were doing.

## How it works

The daemon runs librespot as a Spotify Connect device, cuts its audio into one-second OGG files and writes them into the game's folders. The game script makes the engine load each file and chains it onto the sound already playing. The daemon reads Spotify ahead of real time, so the game has its buffer within a moment of a track change. For YouTube Music, yt-dlp and ffmpeg take the place of librespot. Details in [`docs/HOW-IT-WORKS.md`](docs/HOW-IT-WORKS.md).

## Credits

- The idea of making the game a Spotify Connect device is from [Spotify Radio for Forza Horizon](https://www.nexusmods.com/forzahorizon6/mods/95) by BigJohn ([source](https://github.com/matkhl/FH6-Spotify-Radio)). No code is shared.
- [librespot](https://github.com/librespot-org/librespot) (MIT; licence in the daemon's `librespot` folder)
- [OggVorbisEncoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder) (MIT)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) and FFmpeg (LGPL, builds by [BtbN](https://github.com/BtbN/FFmpeg-Builds)), which the daemon downloads and does not ship
- [xray-monolith](https://github.com/themrdemonized/xray-monolith), whose source shows how the sound engine behaves
- Written with the help of [Claude](https://claude.ai), Anthropic's AI assistant. The PDA tab's layout was made with Claude Design.

## Licence

MIT, see [`LICENSE`](LICENSE).
