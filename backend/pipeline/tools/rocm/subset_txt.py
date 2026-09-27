"""subset_txt.py IN_TXT OUT_TXT KEEP_LIST -- keep only the listed images (and their point tracks)."""
import sys, os
src, dst, keep = sys.argv[1], sys.argv[2], set(open(sys.argv[3]).read().split())
os.makedirs(dst, exist_ok=True)
open(f"{dst}/cameras.txt", "w").write(open(f"{src}/cameras.txt").read())
L = [l for l in open(f"{src}/images.txt") if l[0] != "#"]
ids, out = set(), []
for h, o in zip(L[0::2], L[1::2]):
    if h.split()[9] in keep:
        ids.add(h.split()[0]); out += [h, o]
open(f"{dst}/images.txt", "w").writelines(out)
with open(f"{dst}/points3D.txt", "w") as f:
    for l in open(f"{src}/points3D.txt"):
        if l[0] == "#": continue
        p = l.split(); tr = [(p[i], p[i+1]) for i in range(8, len(p), 2) if p[i] in ids]
        if len(tr) >= 2: f.write(" ".join(p[:8] + [x for t in tr for x in t]) + "\n")
print(len(ids), "images kept")
