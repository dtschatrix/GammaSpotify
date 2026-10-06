# Checks what the PDA showed against what was actually playing. The harness noted, at several
# moments, the chunk the fake engine was in, how far into it, and the track and time on the PDA.
# fake_librespot plays track N at position P from (N * 17 s + P) of the test audio, so the chunk
# is located in the test audio and compared.
import numpy as np, subprocess, os, sys
notes = sys.argv[1]; chunks = notes[:-len(".heard")] + ".chunks"; SR = 44100
st = np.frombuffer(open("test_audio.raw", "rb").read(), "<i2").reshape(-1, 2) / 32767
ref = st.mean(axis=1); n = len(ref); F = np.conj(np.fft.rfft(ref))
worst, rows = 0, 0
for line in open(notes):
    label, name, into, uri, shown = line.rstrip("\n").split("\t")
    y = np.frombuffer(subprocess.run(["oggdec", "-Q", "-R", "-o", "-", os.path.join(chunks, name + ".ogg")], capture_output=True).stdout, "<i2") / 32767
    if len(y) == 2 * SR: y = y.reshape(-1, 2).mean(axis=1)   # head mode chunks are stereo
    # where this chunk sits in the (looping) test audio
    corr = np.fft.irfft(np.fft.rfft(y, n) * F, n)
    claimed = ((int(uri.rsplit(":", 1)[1]) - 1) * 17000 + int(shown)) % 60000
    # The test music repeats its chords, so a chunk can fit about as well a few bars away.
    # Among the places it fits, take the one nearest to what the PDA claims.
    fits = np.flatnonzero(corr >= 0.8 * corr.max())
    want = (claimed - int(into)) * SR / 1000
    at = (n - fits) % n
    at = int(at[np.argmin(np.abs((at - want + n / 2) % n - n / 2))])
    playing = (at / SR * 1000 + int(into)) % 60000
    off = (claimed - playing + 30000) % 60000 - 30000
    worst = max(worst, abs(off)); rows += 1
    print("   %-28s PDA %d:%02d, off by %+.2f s" % (label, int(shown) // 60000, int(shown) // 1000 % 60, off / 1000))
ok = rows >= 8 and worst < 700
print(("PASS" if ok else "FAIL") + " the time on the PDA matches the audio being played: %d samples, worst %.2f s" % (rows, worst / 1000))
sys.exit(0 if ok else 1)
