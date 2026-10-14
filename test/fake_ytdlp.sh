#!/bin/bash
# Stands in for yt-dlp in tests. Answers like the real one asked with -O "%(...)j": one JSON
# line per track. A track's "stream" is a WAV cut from the test audio, so ffmpeg decodes it for
# real. Some ids misbehave on purpose:
#   fail*  cannot be played ("Video unavailable");
#   once*  the first address it gives is refused (no audio at all), the next one works.
HERE="$(cd "$(dirname "$0")" && pwd)"
STATE=${TMPDIR:-/tmp}/fake_ytdlp; mkdir -p "$STATE"
LIST=0
for a in "$@"; do URL="$a"; [ "$a" = "--flat-playlist" ] && LIST=1; done

info() {  # id -> "title|artist|seconds|file"
  case "$1" in
    vid00000001|once*) echo "Creep|Radiohead|40|yt_long.wav";;
    vid00000002) echo "Karma Police|Radiohead|15|yt_short.wav";;
    vid00000003) echo "Lucky|Radiohead|15|yt_short.wav";;
    vid00000004) echo "Exit Music|Radiohead|15|yt_short.wav";;
    vid00000005) echo "No Surprises|Radiohead|15|yt_short.wav";;
    vid00000006) echo "Airbag|Radiohead|15|yt_short.wav";;
    vid00000007) echo "Paranoid Android|Radiohead|15|yt_short.wav";;
    *) echo "Track $1|Someone|15|yt_short.wav";;
  esac
}
entry() {  # id [url]
  IFS='|' read -r title artist secs file <<< "$(info "$1")"
  printf '{"id": "%s", "title": "%s", "artist": "%s", "channel": "%s - Topic", "duration": %s, "url": "%s"}\n' \
    "$1" "$title" "$artist" "$artist" "$secs" "${2-}"
}
file_of() { IFS='|' read -r _ _ _ file <<< "$(info "$1")"; echo "$HERE/$file"; }

if [ $LIST = 1 ]; then
  sleep "${FAKE_YT_LIST_DELAY:-0.4}"
  case "$URL" in
    *list=RD*)            # a radio: the track it was made from first, then similar music
      entry "${URL##*list=RD}"; entry vid00000003; entry vid00000004; entry vid00000005;;
    *list=PLfake1*)       entry vid00000004; entry vid00000005;;
    *browse/MPREb_fake1*) entry vid00000006; entry vid00000007;;
    *) echo "ERROR: [youtube:tab] The playlist does not exist" >&2; exit 1;;
  esac
  exit 0
fi

ID="${URL##*v=}"
sleep "${FAKE_YT_DELAY:-1.2}"
case "$ID" in
  fail*) echo "ERROR: [youtube] $ID: Video unavailable" >&2; exit 1;;
  once*) if [ ! -f "$STATE/$ID" ]; then touch "$STATE/$ID"; entry "$ID" "$HERE/refused.wav"; exit 0; fi;;
esac
entry "$ID" "$(file_of "$ID")"
