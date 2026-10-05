# Synthesizes one minute of stereo "music" (pad, bass, arpeggio, drums, clicks) as raw 16-bit PCM:
# test_audio.raw. It stands in for Spotify in the tests, so no real music is needed.
import numpy as np, os
SR, HOP, XF, N = 44100, 5.0, 0.06, 24
total = int(SR * (HOP * N + XF)); t = np.arange(total) / SR
rng = np.random.default_rng(7)
beat = 60 / 100.0
chords = [[220.00, 261.63, 329.63], [174.61, 220.00, 261.63], [130.81, 164.81, 196.00], [196.00, 246.94, 293.66]]  # Am F C G
x = np.zeros(total)
bar = beat * 4
for b in range(int(t[-1] / bar) + 1):
    ch = chords[b % 4]; s, e = int(b * bar * SR), min(total, int((b + 1) * bar * SR))
    tt = t[s:e] - b * bar
    env = np.minimum(1, tt / 0.3) * np.minimum(1, (bar - tt) / 0.3)
    for f in ch:  # sustained pad: seams are easy to hear on this
        x[s:e] += 0.10 * env * (np.sin(2 * np.pi * f * t[s:e]) + 0.4 * np.sin(2 * np.pi * 2 * f * t[s:e] + 0.3))
    x[s:e] += 0.22 * np.sin(2 * np.pi * ch[0] / 2 * t[s:e]) * env  # bass
    for k in range(8):  # arpeggio
        f = ch[k % 3] * 2; a, z = s + int(k * beat / 2 * SR), min(total, s + int((k * beat / 2 + 0.25) * SR))
        if a < total: x[a:z] += 0.12 * np.sin(2 * np.pi * f * t[a:z]) * np.exp(-(t[a:z] - t[a]) * 9)
for k in range(int(t[-1] / beat) + 1):  # kick + hat
    a = int(k * beat * SR); z = min(total, a + int(0.18 * SR))
    if a < total:
        tt = t[a:z] - t[a]; x[a:z] += 0.5 * np.sin(2 * np.pi * (110 * np.exp(-tt * 18) + 45) * tt) * np.exp(-tt * 16)
    a = int((k + 0.5) * beat * SR); z = min(total, a + int(0.05 * SR))
    if a < total: x[a:z] += 0.08 * rng.standard_normal(z - a) * np.exp(-(t[a:z] - t[a]) * 80)
x = x / np.max(np.abs(x)) * 0.8
SR_ = SR
x = x[:SR_ * 60]
rng = np.random.default_rng(3)
# hard transients at random places and right at chunk starts: the worst case for the encoder
for p in list(rng.integers(0, len(x) - 500, 40)) + [SR_ * 2 * k for k in range(1, 29, 3)]:
    x[p:p + 300] += 0.6 * rng.standard_normal(300) * np.exp(-np.arange(300) / 60)
x = np.clip(x, -0.99, 0.99)
st = np.stack([x, np.roll(x, 300) * 0.8], axis=1)
path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "test_audio.raw")
open(path, "wb").write((st * 32767).astype("<i2").tobytes())
print("wrote", path)
