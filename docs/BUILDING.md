# Building from source

Two things to build: the daemon (C#) and librespot (Rust). The mod is plain script files. Tools are listed under Requirements in the [README](../README.md).

## Daemon

From source: start `daemon\run.bat`, which runs `dotnet run -c Release`.

Stand-alone exe, as in the release zip (no .NET needed to run it; also builds on Linux):

```
cd daemon
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o ..\dist\daemon
```

The daemon looks for `librespot\librespot.exe` and an optional `config.json` in the folder it is started from, then in the exe's folder and its parents. Without a `config.json`, settings go to `%APPDATA%\SpotifyRadio`.

## librespot

The daemon was written against 0.8.0 and uses only its command line (`--backend pipe`, raw 44.1 kHz stereo on stdout).

On Linux, for Windows (how the release exe was built):

```
rustup target add x86_64-pc-windows-gnu
cargo install librespot --version 0.8.0 --locked --target x86_64-pc-windows-gnu \
  --no-default-features --features "native-tls with-libmdns" --root out
x86_64-w64-mingw32-strip out/bin/librespot.exe
```

On Windows (untested):

```
cargo install librespot --version 0.8.0 --locked --no-default-features --features "native-tls with-libmdns" --root out
```

`native-tls` uses the system TLS. `with-libmdns` avoids a Bonjour or Avahi dependency. No audio backend feature is needed; `pipe` is always built in.

Copy the result to `daemon\librespot\librespot.exe`, or set `librespotPath` in `config.json`.

## Tests

Linux only.

```
cd daemon && dotnet build -c Release && cd ../test
./run_tests.sh
```

Runs twice (file link, socket link), about six minutes each; `MODES=file` or `MODES=socket` runs one. `exit=0` after each run means all checks passed. `FAKE_BLOCK=400` or `800` fixes the fake engine's buffer size instead of alternating.

## Release

`tools/package.sh <version> <path to librespot.exe>` (Linux, needs `dotnet` and `zip`) builds two zips into `dist/`:

```
Spotify-Radio-x.y-mod.zip          for MO2 "Install a new mod from archive"
  gamedata/...

Spotify-Radio-x.y-daemon.zip       unpacked anywhere outside MO2
  Spotify-Radio-daemon/
    SpotifyRadio.exe
    run.bat                        starts the exe instead of dotnet run
    config.example.json
    librespot/librespot.exe
    librespot/LICENSE-librespot.txt
    README.md
    LICENSE
```

`gamedata` must be at the root of the mod zip. Otherwise MO2 installs it one folder too deep, flags the mod with a red X and the game never loads it.
