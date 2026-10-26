# Crash mapping, 2 of 2 (env 1, 2026-10-17): name stock-player functions by the unstripped dev player. Windows of W
# instructions (default 5, >= FIX fixed bytes, default 14) with call / jump / RIP-relative / absolute fields masked are searched
# in the dev .text; each hit (<= 5) votes for the dev symbol it lands in. NOTES "Candidate 7", the 14:30 crash.
#   W=5 FIX=14 python3 -I scripts/crash_match.py LinuxPlayer-nondev LinuxPlayer-dev 0x9849a0:0x985574 ...  (crash_fde.py ranges)
import re,subprocess,sys,bisect,struct,collections
stock,dev=sys.argv[1],sys.argv[2]; funcs=[tuple(int(x,16) for x in f.split(':')) for f in sys.argv[3:]]
def sections(p):
    out=subprocess.run(['readelf','-SW',p],capture_output=True,text=True).stdout
    for l in out.splitlines():
        m=re.search(r'\]\s+\.text\s+\w+\s+([0-9a-f]+)\s+([0-9a-f]+)\s+([0-9a-f]+)',l)
        if m: return int(m.group(1),16),int(m.group(2),16),int(m.group(3),16)
dva,doff,dsz=sections(dev)
dbytes=open(dev,'rb').read()[doff:doff+dsz]
syms=[]
for l in subprocess.run(['nm','-S','--defined-only','-C',dev],capture_output=True,text=True).stdout.splitlines():
    p=l.split(' ',3)
    if len(p)==4 and p[2] in 'tTwW':
        syms.append((int(p[0],16),int(p[1],16),p[3]))
syms.sort(); starts=[s[0] for s in syms]
def symat(a):
    i=bisect.bisect_right(starts,a)-1
    while i>=0 and not (syms[i][0]<=a<syms[i][0]+max(syms[i][1],1)): i-=1
    return syms[i][2] if i>=0 else '?'
for s,e in funcs:
    out=subprocess.run(['objdump','-d','--insn-width=16',f'--start-address={s}',f'--stop-address={e}',stock],capture_output=True,text=True).stdout
    ins=[]
    for l in out.splitlines():
        m=re.match(r'\s+([0-9a-f]+):\t((?:[0-9a-f]{2} )+)\s*\t?(.*)',l)
        if not m: continue
        a=int(m.group(1),16); b=bytes.fromhex(m.group(2).replace(' ','')); txt=m.group(3)
        mask=[False]*len(b)
        def maskval(v):
            enc=struct.pack('<i',v) if -2**31<=v<2**31 else struct.pack('<I',v&0xffffffff)
            k=b.find(enc)
            if k>=0:
                for j in range(k,k+4): mask[j]=True
        mt=re.search(r'# ([0-9a-f]+)',txt)
        if '(%rip)' in txt and mt: maskval(int(mt.group(1),16)-(a+len(b)))
        if re.match(r'(call|jmp|j[a-z]+)\s',txt):
            n=1 if (len(b)==2 or (len(b)==3 and b[0]==0xf3)) else 4
            for j in range(len(b)-n,len(b)): mask[j]=True
        for im in re.findall(r'\$0x([0-9a-f]+)',txt):
            v=int(im,16)
            if 0x400000<=v<0x8000000: maskval(v)
        ins.append((a,b,mask))
    votes=collections.Counter(); nwin=0; W=int(__import__("os").environ.get("W","5"))
    for i in range(0,max(len(ins)-W,1),3):
        win=ins[i:i+W]; pat=b''; fixed=0
        for a,b,mask in win:
            for j,c in enumerate(b):
                if mask[j]: pat+=b'.'
                else: pat+=re.escape(bytes([c])); fixed+=1
        if fixed<int(__import__("os").environ.get("FIX","14")): continue
        nwin+=1
        hits=[mm.start() for mm in re.finditer(pat,dbytes,re.S)]
        if 0<len(hits)<=5:
            for h in hits: votes[symat(dva+h)[:150]]+=1./len(hits)
    print(f'stock {s:#x}-{e:#x}: {nwin} windows; top: ' + ' || '.join(f'{v:.0f} {k}' for k,v in votes.most_common(3)))
