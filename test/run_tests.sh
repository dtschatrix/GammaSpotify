#!/bin/bash
# See docs/BUILDING.md. Build the daemon first: (cd ../daemon && dotnet build -c Release)
S=${TMPDIR:-/tmp}/spotify-radio-test; mkdir -p "$S"
[ -f test_audio.raw ] || python3 make_test_audio.py
DLL=../daemon/bin/Release/net7.0/SpotifyRadio.dll
for MODE in ${MODES:-file socket}; do
  echo "================ $MODE"
  export SPOTIFY_RADIO_HOME=$S/home_$MODE IPC_DIR=$S/ipc_$MODE KEEP_DIR=$S/keep_$MODE; rm -rf $SPOTIFY_RADIO_HOME $IPC_DIR; mkdir -p $SPOTIFY_RADIO_HOME $IPC_DIR
  echo "{\"clientId\":\"testclient\",\"apiBase\":\"http://127.0.0.1:47900/v1\",\"accountsBase\":\"http://127.0.0.1:47900\",\"pollSeconds\":1,\"awayPauseSeconds\":5,\"ipcDir\":\"$IPC_DIR\",\"librespotPath\":\"/usr/bin/python3\",\"librespotArgs\":[\"fake_librespot.py\"]}" > $SPOTIFY_RADIO_HOME/config.json
  echo '{"accessToken":"tok1","refreshToken":"refresh1","expiresAtUtc":"2099-01-01T00:00:00Z"}' > $SPOTIFY_RADIO_HOME/tokens.json
  echo "stale line" > $IPC_DIR/spotify_radio_out.txt; echo "next" > $IPC_DIR/spotify_radio_cmd.txt
  python3 mock_spotify.py & MOCK=$!; sleep 0.5
  dotnet $DLL > daemon_$MODE.log 2>&1 & for i in $(seq 50); do grep -q "files in" daemon_$MODE.log && break; sleep 0.2; done
  luajit harness_ui.lua "../mod/Spotify Radio/gamedata" $MODE 2>&1 | grep -v "^\[spotify_radio\] Spotify\|^HUD>"; echo "exit=${PIPESTATUS[0]}"
  pkill -f SpotifyRadio.dll; pkill -f fake_librespot.py; kill $MOCK 2>/dev/null; sleep 0.5
  python3 audio_check.py $KEEP_DIR
  head -6 daemon_$MODE.log
done
