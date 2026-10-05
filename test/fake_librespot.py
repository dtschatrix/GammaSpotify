# Stands in for librespot in tests: writes stereo 16-bit PCM to stdout as fast as the reader takes it.
import sys, os
data = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "test_audio.raw"), "rb").read()
out = sys.stdout.buffer
try:
    while True:
        for i in range(0, len(data), 3001):       # odd-sized writes: the reader must cope with split frames
            out.write(data[i:i + 3001]); out.flush()
except (BrokenPipeError, KeyboardInterrupt):
    pass
