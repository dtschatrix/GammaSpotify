# Checks the daemon's first run and the yt-dlp / ffmpeg download against fake GitHub releases
# served from here: what is downloaded, checked and unpacked, and what the first-run questions
# do with the answers. Needs the daemon built (see run_tests.sh).
import hashlib, http.server, io, json, os, shutil, subprocess, sys, threading, time, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "..", "daemon", "bin", "Release", "net7.0", "SpotifyRadio.dll")
S = os.path.join(os.environ.get("TMPDIR", "/tmp"), "spotify-radio-test", "setup")
PORT = 47910
FF = "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0"
fails = 0

def check(cond, what):
    global fails
    print(("PASS " if cond else "FAIL ") + what)
    if not cond: fails += 1

def zip_bytes(files):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in files.items(): z.writestr(name, data)
    return buf.getvalue()

def sha(b): return hashlib.sha256(b).hexdigest()

# ---------------------------------------------------------------- fake releases
shutil.rmtree(S, ignore_errors=True)
rel = os.path.join(S, "rel"); os.makedirs(os.path.join(rel, "yt")); os.makedirs(os.path.join(rel, "ff"))
ytzip = zip_bytes({"yt-dlp.exe": b"fake yt-dlp", "_internal/python3.dll": b"x"})
ffzip = zip_bytes({FF + "/LICENSE.txt": b"LGPL", FF + "/bin/ffmpeg.exe": b"fake ffmpeg", FF + "/bin/avcodec-63.dll": b"dll",
                   FF + "/bin/ffplay.exe": b"not wanted", FF + "/doc/ffmpeg.html": b"not wanted", FF + "/include/x.h": b"not wanted"})
old = zip_bytes({"ffmpeg-n8.1/bin/ffmpeg.exe": b"older"})
open(os.path.join(rel, "yt", "yt-dlp_win.zip"), "wb").write(ytzip)
open(os.path.join(rel, "ff", FF + ".zip"), "wb").write(ffzip)
open(os.path.join(rel, "ff", "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"), "wb").write(old)
def write_sums(yt_ok=True):
    open(os.path.join(rel, "yt", "SHA2-256SUMS"), "w").write(
        f"{sha(ytzip) if yt_ok else '0' * 64}  yt-dlp_win.zip\n{'1' * 64}  yt-dlp.exe\n")
    open(os.path.join(rel, "ff", "checksums.sha256"), "w").write(
        f"{sha(old)}  ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip\n{sha(ffzip)}  {FF}.zip\n{'2' * 64}  ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz\n")
write_sums()

class Quiet(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=rel, **k)
    def log_message(self, *a): pass
server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Quiet)
threading.Thread(target=server.serve_forever, daemon=True).start()

def env(home):
    e = {k: v for k, v in os.environ.items() if not k.startswith("SPOTIFY_RADIO_")}
    e.update(SPOTIFY_RADIO_HOME=home, SPOTIFY_RADIO_YTDLP_RELEASES=f"http://127.0.0.1:{PORT}/yt/",
             SPOTIFY_RADIO_FFMPEG_RELEASES=f"http://127.0.0.1:{PORT}/ff/")
    return e

def app_dir(name):
    d = os.path.join(S, name); os.makedirs(os.path.join(d, "librespot"))   # the daemon's own folder is found by its librespot folder
    os.makedirs(os.path.join(d, "home"))
    return d

def run(d, args=(), stdin="", wait_for=None, timeout=60):
    """Runs the daemon in folder d; returns (exit code or None if it was stopped, output)."""
    p = subprocess.Popen(["dotnet", DLL, *args], cwd=d, env=env(os.path.join(d, "home")), text=True,
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = []
    t = threading.Thread(target=lambda: [out.append(l) for l in p.stdout], daemon=True); t.start()
    p.stdin.write(stdin); p.stdin.close()
    end = time.time() + timeout
    while time.time() < end and p.poll() is None:
        if wait_for and any(wait_for in l for l in out): break
        time.sleep(0.1)
    code = p.poll()
    if code is None: p.kill(); p.wait()
    t.join(2)
    return code, "".join(out)

def files(d):
    return sorted(os.path.relpath(os.path.join(r, f), d) for r, _, fs in os.walk(d) for f in fs)

# ---------------------------------------------------------------- --install-youtube
a = app_dir("install")
code, out = run(a, ["--install-youtube"])
yt = os.path.join(a, "yt")
got = files(yt) if os.path.isdir(yt) else []
check(code == 0 and "ready" in out, "--install-youtube downloads both and says so (exit %s)" % code)
check(got == ["ffmpeg/LICENSE-ffmpeg.txt", "ffmpeg/avcodec-63.dll", "ffmpeg/ffmpeg.exe", "yt-dlp_win/_internal/python3.dll", "yt-dlp_win/yt-dlp.exe"],
      "it unpacks yt-dlp, and of ffmpeg only ffmpeg.exe, its libraries and the licence, from the newest stable build: %s" % got)
check(open(os.path.join(yt, "ffmpeg", "ffmpeg.exe"), "rb").read() == b"fake ffmpeg", "the 9.0 build was chosen over 8.1")

# a download that does not match its checksum changes nothing
write_sums(yt_ok=False)
code, out = run(a, ["--update-youtube"])
check(code == 1 and "checksum" in out, "a yt-dlp that does not match the published checksum is refused (exit %s)" % code)
check(files(yt) == got, "and the installed copy is left as it was, without leftovers: %s" % files(yt))
write_sums()

# ---------------------------------------------------------------- first run
# YouTube only, download declined: nothing to play
b = app_dir("first_no")
code, out = run(b, stdin="2\nn\n")
cfg = json.load(open(os.path.join(b, "home", "config.json")))
check(code == 1 and "nothing to play" in out and "Client ID" not in out,
      "first run, YouTube only, download declined: no Spotify questions, and it stops saying why (exit %s)" % code)
check(cfg.get("sources") == ["youtube"], "the choice is kept: %s" % cfg.get("sources"))

# YouTube only, download accepted with Enter: it downloads, asks for the game and starts
c = app_dir("first_yes")
os.makedirs(os.path.join(c, "game", "appdata"))
code, out = run(c, stdin="2\n\n" + os.path.join(c, "game") + "\n", wait_for="Waiting for the game")
cfg = json.load(open(os.path.join(c, "home", "config.json")))
check(code is None and "yt-dlp and ffmpeg are ready" in out and "Waiting for the game" in out and "Client ID" not in out,
      "first run, YouTube only: downloads, asks only for the game folder, and runs")
check(os.path.isfile(os.path.join(c, "yt", "ffmpeg", "ffmpeg.exe")) and cfg.get("ipcDir", "").endswith("appdata") and cfg.get("clientId", "") == "",
      "the tools are in its yt folder and the config has no Spotify in it")

# an existing Spotify setup from before YouTube: Enter keeps Spotify, nothing is downloaded
d = app_dir("upgrade")
json.dump({"clientId": "abc", "ipcDir": os.path.join(d, "appdata")}, open(os.path.join(d, "home", "config.json"), "w"))
os.makedirs(os.path.join(d, "appdata"))
code, out = run(d, stdin="\n", wait_for="Config:", timeout=20)
cfg = json.load(open(os.path.join(d, "home", "config.json")))
check(cfg.get("sources") == ["spotify"] and cfg.get("clientId") == "abc" and "Choice [1]" in out and not os.path.isdir(os.path.join(d, "yt")),
      "a Spotify user from before is asked once, Enter keeps Spotify: %s" % cfg.get("sources"))

server.shutdown()
print("setup checks: %d failed" % fails)
sys.exit(1 if fails else 0)
