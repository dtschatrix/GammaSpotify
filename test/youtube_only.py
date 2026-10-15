# A daemon set up for YouTube only (no Spotify config, no librespot), driven over the file link
# the way the game drives it: hello, search, stream, play, pause, next into the radio, stop.
# Uses fake_ytdlp.sh, the real ffmpeg and mock_spotify.py for YouTube Music's search.
import json, os, re, shutil, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "..", "daemon", "bin", "Release", "net7.0", "SpotifyRadio.dll")
S = os.path.join(os.environ.get("TMPDIR", "/tmp"), "spotify-radio-test", "yt_only")
fails = 0

def check(cond, what):
    global fails
    print(("PASS " if cond else "FAIL ") + what)
    if not cond: fails += 1

shutil.rmtree(S, ignore_errors=True)
home, appdata, level = os.path.join(S, "home"), os.path.join(S, "game", "appdata"), os.path.join(S, "game", "gamedata", "levels", "l01")
for d in (home, appdata, level): os.makedirs(d)
json.dump({"sources": ["youtube"], "ipcDir": appdata, "pollSeconds": 1}, open(os.path.join(home, "config.json"), "w"))
cmd, out_path = os.path.join(appdata, "spotify_radio_cmd.txt"), os.path.join(appdata, "spotify_radio_out.txt")

env = dict(os.environ, SPOTIFY_RADIO_HOME=home, SPOTIFY_RADIO_YTDLP=os.path.join(HERE, "fake_ytdlp.sh"),
           SPOTIFY_RADIO_FFMPEG=shutil.which("ffmpeg"), SPOTIFY_RADIO_YTM_BASE="http://127.0.0.1:47900")
mock = subprocess.Popen([sys.executable, os.path.join(HERE, "mock_spotify.py")], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(0.5)
log = open(os.path.join(S, "daemon.log"), "w")
daemon = subprocess.Popen(["dotnet", DLL], cwd=S, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)

lines, offset = [], 0
def read_out():
    global offset
    if not os.path.exists(out_path): return
    data = open(out_path, "rb").read()
    new, offset = data[offset:], len(data)
    for l in new.decode("ascii", "replace").splitlines():
        if l.strip(): lines.append(l.split("\t"))
def send(line):
    with open(cmd, "a") as f: f.write(line + "\n")
def wait(pred, secs):
    end = time.time() + secs
    while time.time() < end:
        read_out()
        hit = next((l for l in lines if pred(l)), None)
        if hit: return hit
        time.sleep(0.1)
    return None

try:
    for _ in range(100):
        if "files in" in open(os.path.join(S, "daemon.log")).read(): break
        time.sleep(0.2)
    send("hello")
    state = wait(lambda l: l[0] == "state", 10)
    check(wait(lambda l: l[0] == "hello", 1) is not None and state and state[1] == "idle" and state[7] == "YouTube",
          "YouTube only: the daemon starts without any Spotify setup and says YouTube is idle: %s" % state)
    check(state and state[10:12] == ["youtube", "youtube"], "the state says YouTube is on and there is nothing to switch to: %s" % (state and state[10:]))

    lines.clear(); send("search creep")
    end = wait(lambda l: l[0] == "results_end", 10)
    uris = [l[3] for l in lines if l[0] == "result"]
    check(end and uris[:3] == ["yt:video:vid00000001", "yt:video:vid00000002", "yt:video:failfailfai"] and "yt:album:MPREb_fake1" in uris,
          "a plain search goes to YouTube Music: %s" % uris)

    send("stream start head " + level)
    started = wait(lambda l: l[:2] == ["stream", "started"], 10)
    check(started is not None, "the game gets the stream: %s" % started)
    alive = True
    def game():                                   # what the game sends while it plays
        while alive:
            send("buf 0 -1"); time.sleep(0.5)
    threading.Thread(target=game, daemon=True).start()

    lines.clear(); t0 = time.time(); send("playuri yt:video:vid00000001")
    mark = wait(lambda l: l[0] == "track" and l[4] == "yt:video:vid00000001", 10)
    chunk = wait(lambda l: l[0] == "chunk", 10)
    check(mark and mark[5:7] == ["Radiohead", "Creep"] and int(mark[2]) < 1000 and chunk is not None,
          "playing a result: chunks and the track mark from its start, %.1f s after the click: %s" % (time.time() - t0, mark))
    time.sleep(2)
    files = [f for f in os.listdir(level) if f.endswith(".ogg")]
    check(len(files) >= 5, "chunk files appear in the level folder: %d" % len(files))

    lines.clear(); send("pause")
    hold = wait(lambda l: l[:2] == ["audio", "hold"], 3)
    time.sleep(1.5); read_out(); lines.clear(); time.sleep(2); read_out()
    check(hold is not None and not any(l[0] == "chunk" for l in lines), "pause: the game is told to hold, and no audio comes while paused")
    send("play")
    check(wait(lambda l: l[:2] == ["audio", "resume"], 3) is not None and wait(lambda l: l[0] == "chunk", 5) is not None,
          "play: the game is told to carry on and audio comes again")

    lines.clear(); send("next")
    mark = wait(lambda l: l[0] == "track" and l[4] != "yt:video:vid00000001", 12)
    check(mark and mark[4] == "yt:video:vid00000003" and wait(lambda l: l[:2] == ["audio", "flush"], 1),
          "next at the end of the list: a radio of the track plays its first new track: %s" % (mark and mark[4]))

    lines.clear(); send("state")
    state = wait(lambda l: l[0] == "state" and l[1] == "playing", 5)
    check(state and state[9] == "yt:video:vid00000003" and state[7] == "YouTube", "the state says so: %s" % (state and state[9]))

    lines.clear(); send("playuri spotify:track:1")
    err = wait(lambda l: l[0] == "error", 5)
    check(err and "Spotify is not set up" in err[1], "a Spotify address gets a clear error: %s" % (err and err[1]))
    lines.clear(); send("source spotify")
    err = wait(lambda l: l[0] == "error", 5)
    check(err and "Spotify is not set up" in err[1], "and so does the switch: %s" % (err and err[1]))

    alive = False
    send("stream stop")
    check(wait(lambda l: l[:2] == ["stream", "stopped"], 5) is not None, "the stream stops")
    text = open(os.path.join(S, "daemon.log")).read()
    check("Fatal" not in text and "librespot" not in text and "Spotify:" not in text,
          "the daemon log has no Spotify or librespot in it")
finally:
    daemon.kill(); mock.kill()
    subprocess.run(["pkill", "-f", "yt_(long|short)[.]wav"])
print("youtube-only checks: %d failed" % fails)
sys.exit(1 if fails else 0)
