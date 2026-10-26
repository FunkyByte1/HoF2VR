import os, re, sys
D = sys.argv[1]
nodes = {}
for fn in os.listdir(D):
    if not fn.endswith('.txt'): continue
    pid = int(fn[:-4])
    with open(os.path.join(D, fn), errors='replace') as f: s = f.read()
    m = re.search(r'string m_Name = "([^"]*)"', s)
    if not m: continue
    name = m.group(1)
    desc = re.search(r'string Description = "([^"]*)"', s)
    # children: PPtr entries inside "Children" array (FileID 0)
    kids = []
    cm = re.search(r'VignetteNode\[\] Children\n(.*?)\n\t(?!\t)', s, re.S)
    if cm:
        kids = [int(x) for x in re.findall(r'm_FileID = 0\n\s*SInt64 m_PathID = (\d+)', cm.group(1))]
    data = [int(x) for x in re.findall(r'PPtr<VignetteNode> data\n\s*int m_FileID = 0\n\s*SInt64 m_PathID = (\d+)', s)]
    vars_ = re.findall(r'PPtr<(\w+Variable)> Variable\n\s*int m_FileID = (\d+)\n\s*SInt64 m_PathID = (\d+)', s)
    extra = {k: v for k, v in re.findall(r'\n\t(?:float|bool|SInt32) (\w+) = ([^\n]+)', s) if k in ('maxDegrees','timeout','invert','DecoratorBehavior','OverrideDuration','Mode','Comparison','Operation')}
    nodes[pid] = dict(name=name, desc=desc.group(1) if desc else '', kids=kids + data, vars=vars_, extra=extra)
parent = {}
for p, n in nodes.items():
    for k in n['kids']: parent.setdefault(k, []).append(p)
def lab(p):
    n = nodes.get(p, {'name': '?', 'desc': '', 'vars': [], 'extra': {}})
    v = ' '.join('%s(%s:%s)' % (a[0].replace('Variable',''), a[1], a[2]) for a in n['vars'])
    return '%d %s%s %s %s' % (p, n['name'], (' "' + n['desc'] + '"') if n['desc'] else '', v, n['extra'] if n['extra'] else '')
targets = [p for p, n in nodes.items() if any(a[2] == sys.argv[2] and a[1] == sys.argv[3] for a in n['vars'])]
for t in sorted(targets):
    chain = [t]; cur = t
    while cur in parent and len(chain) < 12:
        cur = parent[cur][0]; chain.append(cur)
    print('== target', lab(t))
    for depth, c in enumerate(chain[1:6], 1):
        print('  ' * depth + '^ ' + lab(c))
        if depth <= int(os.environ.get("SIBD", "2")):
            for k in nodes.get(c, {}).get('kids', []):
                if k != chain[depth-1]: print('  ' * depth + '   - sib ' + lab(k))
