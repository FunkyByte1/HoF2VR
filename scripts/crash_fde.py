# Crash mapping, 1 of 2 (env 1, 2026-10-17): the stock player's functions (.eh_frame FDEs) that hold the given addresses.
#   python3 -I scripts/crash_fde.py downloads/unity-linux-support/LinuxPlayer-nondev 0x984a08 ...  (absolute: + 0x400000)
import re,subprocess,sys,bisect
exe=sys.argv[1]; addrs=[int(a,16) for a in sys.argv[2:]]
out=subprocess.run(['readelf','--debug-dump=frames',exe],capture_output=True,text=True).stdout
r=[]
for m in re.finditer(r'FDE cie=\w+ pc=([0-9a-f]+)\.\.([0-9a-f]+)',out):
    r.append((int(m.group(1),16),int(m.group(2),16)))
r.sort(); starts=[a for a,_ in r]
print('fdes',len(r))
for a in addrs:
    i=bisect.bisect_right(starts,a)-1
    s,e=r[i] if i>=0 else (0,0)
    print(hex(a),'in' if s<=a<e else 'NOT in',hex(s),hex(e),'size',e-s,'off',a-s)
