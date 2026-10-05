# Decodes the chunks the "engine" was given, in order, and checks they form one continuous
# stretch of the source audio (mono mix), i.e. nothing lost, repeated or shifted between chunks.
import numpy as np, subprocess, os, sys
keep=sys.argv[1]; SR=44100
st=np.frombuffer(open("test_audio.raw","rb").read(),"<i2").reshape(-1,2)/32767
ref=st.mean(axis=1); ref=np.concatenate([ref,ref,ref])
parts=[]
for f in sorted(os.listdir(keep)):
    c=np.frombuffer(subprocess.run(["oggdec","-Q","-R","-o","-",os.path.join(keep,f)],capture_output=True).stdout,"<i2")/32767
    assert len(c)==SR, (f,len(c)); parts.append(c)
y=np.concatenate(parts)
# locate the start of the stream in the source
probe=y[2000:2000+32768]; n=len(st)
corr=np.fft.irfft(np.fft.rfft(ref[:n+40000],n+40000)*np.conj(np.fft.rfft(probe,n+40000)))
off=int(np.argmax(corr))-2000
r=ref[off:off+len(y)] if off>=0 else None
err=y-r
seams=np.concatenate([err[i*SR-64:i*SR+64] for i in range(1,len(parts))])
ok = np.sqrt((err**2).mean())<0.01
print(("PASS" if ok else "FAIL")+" audio check: %d chunks = %.0f s, continuous with the source from %.2f s; rms err %.4f, at joins %.4f, signal rms %.3f"%(len(parts),len(y)/SR,off/SR,np.sqrt((err**2).mean()),np.sqrt((seams**2).mean()),np.sqrt((r**2).mean())))
sys.exit(0 if ok else 1)
