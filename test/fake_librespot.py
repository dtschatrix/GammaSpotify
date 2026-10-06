# Stands in for librespot in tests. Writes stereo 16-bit PCM to stdout as fast as the reader
# takes it, and behaves like the real one where the daemon depends on it:
#   - silent unless Spotify (mock_spotify.py) says the game's device is playing;
#   - a skip, seek or transfer is followed by a short silence while the track "loads";
#   - reports its position when it starts, resumes or has fallen behind; never when it is ahead;
#   - at the end of a track goes straight on to the next.
# Track N at position P plays the test audio from (N * 17 s + P), so tests can tell what was played.
import sys, os, time, json, threading, urllib.request
BASE = "http://127.0.0.1:47900"
RATE = 44100 * 4
data = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "test_audio.raw"), "rb").read()
out = sys.stdout.buffer
try:
    import fcntl; fcntl.fcntl(1, 1031, 4096)   # F_SETPIPE_SZ: a pipe as small as on Windows, so nothing lingers in it
except Exception: pass
ctl = None

def get(path):
    return json.loads(urllib.request.urlopen(BASE + path, timeout=2).read() or b"{}")
def poll():
    global ctl
    while True:
        try: ctl = get("/_ctl")
        except Exception: pass
        time.sleep(0.03)
threading.Thread(target=poll, daemon=True).start()

gen, idx, pos, written, playing = None, 0, 0, 0, False     # pos = bytes into the track
try:
    while True:
        c = ctl
        if not c or not c["playing"] or c["device"] != "pda":
            playing = False; time.sleep(0.02); continue
        if c["gen"] != gen:
            out.write(b"\0" * (-written % 4)); written += -written % 4   # audio always ends on a whole frame
            out.flush(); time.sleep(0.4)                                   # loading
            c = get("/_ctl"); gen, idx = c["gen"], c["idx"]
            pos = int(c["pos"] * RATE / 1000) // 4 * 4
            get("/_report?pos=%d" % (pos * 1000 // RATE)); playing = True
        elif not playing:
            get("/_report?pos=%d" % (pos * 1000 // RATE)); playing = True
        elif c["idx"] == idx and c["progress"] - pos * 1000 // RATE > 1000:
            get("/_report?pos=%d" % (pos * 1000 // RATE))                  # fell behind: say where we really are
        at = (idx * 17 * RATE + pos) % len(data)
        block = data[at:at + 3001]                 # odd-sized writes: the reader must cope with split frames
        out.write(block); out.flush(); pos += len(block); written += len(block)
        if pos * 1000 // RATE >= c["durations"][idx]:
            pos = pos // 4 * 4
            out.write(b"\0" * (-written % 4)); written += -written % 4
            idx, pos = (idx + 1) % len(c["durations"]), 0
            get("/_advance")
except (BrokenPipeError, KeyboardInterrupt):
    pass
