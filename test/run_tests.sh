#!/bin/bash
# See docs/BUILDING.md. Build the daemon first: (cd ../daemon && dotnet build -c Release)
S=${TMPDIR:-/tmp}/spotify-radio-test; mkdir -p "$S"
[ -f test_audio.raw ] || python3 make_test_audio.py
# YouTube: fake_ytdlp.sh stands in for yt-dlp; ffmpeg is the real one, decoding WAVs cut from the test audio
[ -f yt_long.wav ] || ffmpeg -loglevel error -y -f s16le -ar 44100 -ac 2 -i test_audio.raw -t 40 yt_long.wav
[ -f yt_short.wav ] || ffmpeg -loglevel error -y -f s16le -ar 44100 -ac 2 -ss 20 -i test_audio.raw -t 15 yt_short.wav
chmod +x fake_ytdlp.sh   # the daemon runs it directly, and git on Windows does not keep the mode
export SPOTIFY_RADIO_YTDLP=$PWD/fake_ytdlp.sh SPOTIFY_RADIO_FFMPEG=$(command -v ffmpeg) SPOTIFY_RADIO_YTM_BASE=http://127.0.0.1:47900
DLL=../daemon/bin/Release/net7.0/SpotifyRadio.dll
for MODE in ${MODES:-file socket}; do
  echo "================ $MODE"
  export SPOTIFY_RADIO_HOME=$S/home_$MODE IPC_DIR=$S/ipc_$MODE KEEP_DIR=$S/keep_$MODE; rm -rf $SPOTIFY_RADIO_HOME $IPC_DIR; mkdir -p $SPOTIFY_RADIO_HOME $IPC_DIR
  echo "{\"clientId\":\"testclient\",\"apiBase\":\"http://127.0.0.1:47900/v1\",\"accountsBase\":\"http://127.0.0.1:47900\",\"pollSeconds\":1,\"ipcDir\":\"$IPC_DIR\",\"librespotPath\":\"/usr/bin/python3\",\"librespotArgs\":[\"fake_librespot.py\"]}" > $SPOTIFY_RADIO_HOME/config.json
  echo '{"accessToken":"tok1","refreshToken":"refresh1","expiresAtUtc":"2099-01-01T00:00:00Z"}' > $SPOTIFY_RADIO_HOME/tokens.json
  rm -rf "${TMPDIR:-/tmp}/fake_ytdlp"
  echo "stale line" > $IPC_DIR/spotify_radio_out.txt; echo "next" > $IPC_DIR/spotify_radio_cmd.txt
  python3 mock_spotify.py & MOCK=$!; sleep 0.5
  dotnet $DLL > daemon_$MODE.log 2>&1 & for i in $(seq 50); do grep -q "files in" daemon_$MODE.log && break; sleep 0.2; done
  luajit harness_ui.lua "../mod/Spotify Radio/gamedata" $MODE 2>&1 | grep -v "^\[spotify_radio\] Spotify\|^HUD>"; echo "exit=${PIPESTATUS[0]}"
  pkill -f SpotifyRadio.dll; pkill -f fake_librespot.py; pkill -f "yt_(long|short)[.]wav"; kill $MOCK 2>/dev/null; sleep 0.5
  python3 audio_check.py $KEEP_DIR
  python3 heard_check.py $KEEP_DIR.heard
  head -6 daemon_$MODE.log
done
