import os, re, sys
exec(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'bt.py')).read().split('parent = {}')[0])
parent = {}
for p, n in nodes.items():
    if n['name'] == 'KnightCombatantBehavior': continue
    for k in n['kids']: parent.setdefault(k, []).append(p)
def short(p):
    n = nodes.get(p, {'name':'?','desc':'','vars':[],'extra':{}})
    v = ','.join('%s:%s' % (a[1], a[2]) for a in n['vars'] if a[2] != '0')
    return '%d:%s%s%s%s' % (p, n['name'].replace('Node',''), ('"'+n['desc']+'"') if n['desc'] else '', ('['+v+']') if v else '', ('{D'+n['extra'].get('DecoratorBehavior','0')+'}') if n['extra'].get('DecoratorBehavior','0')!='0' else '')
for t in sorted(p for p, n in nodes.items() if n['name'] == 'TimerStartNode' and any(a[2]=='534' and a[1]=='6' for a in n['vars'])):
    chain=[t]; cur=t
    while cur in parent and len(chain)<9: cur=parent[cur][0]; chain.append(cur)
    print('== start', t, ' <- '.join(short(c) for c in chain[1:]))
    for c in chain[1:4]:
        print('   kids of %d: %s' % (c, ' | '.join(short(k) for k in nodes[c]['kids'])))
