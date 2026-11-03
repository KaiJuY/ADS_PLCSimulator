# Builds EcidOccurrences.golden.tsv and EcidMap.golden.tsv for #28 from bed1ac8 and ecidmap.tsv.
# Run from the repository root: python .adf/evidence/28/make-goldens.py
import json, subprocess

def show(path):
    return json.loads(subprocess.check_output(['git', 'show', 'bed1ac8:' + path]).decode('utf-8'))

def esc(k):
    return str(k).replace('~', '~0').replace('/', '~1')

rows = [l.rstrip('\r\n').split('\t') for l in open('.adf/evidence/28/ecidmap.tsv', encoding='utf-8')][1:]
new_of = {int(r[0]): int(r[3]) for r in rows}

occ = []
def walk(node, ptr):
    if isinstance(node, dict):
        for k, v in node.items():
            p = ptr + '/' + esc(k)
            if k == 'ecid' and isinstance(v, int):
                occ.append((p, v))
            elif k == 'ecidBy':
                for vk, vv in v['values'].items():
                    occ.append((p + '/values/' + esc(vk), vv))
            else:
                walk(v, p)
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, ptr + '/' + str(i))

walk(show('projects/Polar2Chamber/config/service-behaviors.json'), '')
with open('.adf/evidence/28/EcidOccurrences.golden.tsv', 'w', encoding='utf-8', newline='\n') as f:
    for p, old in occ:
        f.write(f'{p}\t{old}\t{new_of[old]}\n')

cfg = show('projects/Polar2Chamber/config/module-config.json')
params = None
def find(n):
    global params
    if isinstance(n, dict):
        if 'configParamList' in n: params = n['configParamList']; return
        for v in n.values(): find(v)
    elif isinstance(n, list):
        for v in n: find(v)
find(cfg)
dtype = {p['id']: p['dataType'] for p in params}
with open('.adf/evidence/28/EcidMap.golden.tsv', 'w', encoding='utf-8', newline='\n') as f:
    for r in rows:
        f.write(f'{r[0]}\t{r[1]}\t{r[2]}\t{dtype[int(r[0])]}\t{r[3]}\t{r[4]}\n')
print(len(occ), sum(1 for p, _ in occ if '/ecidBy/' in p), len(set(o for _, o in occ)), len(rows))
