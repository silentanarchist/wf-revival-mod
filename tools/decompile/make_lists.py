#!/usr/bin/env python3
"""From Il2CppDumper's script.json and the type model, make the lists the Ghidra scripts and the decompile need.
Usage: make_lists.py <script.json> <model.json> <kinds from model-all.json> <out dir>
Writes: methods.txt (addr size name) for ApplyNames.py, metadata.txt and strings.txt (addr name) for ApplyNames.py,
        sigs.tsv for SetSigs.py, funcs.tsv for DecompAll.py."""
import json, re, sys, os
script, model_path, all_path, out = sys.argv[1:5]
os.makedirs(out, exist_ok=True)
d = json.load(open(script))
# methods: every compiled method, size = distance to the next one
ms = sorted((e["Address"], e["Name"]) for e in d["ScriptMethod"])
with open(os.path.join(out, "methods.txt"), "w") as f:
    for i, (a, n) in enumerate(ms):
        size = (ms[i + 1][0] - a) if i + 1 < len(ms) else 16
        f.write("%x\t%d\t%s\n" % (a, max(size, 4), n))
with open(os.path.join(out, "metadata.txt"), "w") as f:
    for e in d["ScriptMetadata"]: f.write("%x\t%s\n" % (e["Address"], e["Name"]))
    for e in d["ScriptMetadataMethod"]: f.write("%x\t%s\n" % (e["Address"], e["Name"]))
with open(os.path.join(out, "strings.txt"), "w") as f:
    for e in d["ScriptString"]:
        f.write("%x\tstr_%s\n" % (e["Address"], re.sub(r"[^A-Za-z0-9_]", "_", e["Value"])[:40]))
# signatures and function list for the game's own methods
m = json.load(open(model_path))["types"]
kinds = {}
for full, t in json.load(open(all_path))["types"].items():
    kinds[full] = t["kind"]; kinds.setdefault(t["name"], t["kind"])
prim = {"void": "void", "int": "int", "uint": "uint", "bool": "bool", "float": "float", "double": "double", "long": "longlong", "ulong": "ulonglong",
        "byte": "byte", "sbyte": "sbyte", "short": "short", "ushort": "ushort", "char": "ushort", "IntPtr": "longlong", "UIntPtr": "ulonglong"}
def gtype(t):
    t = t.replace("ref ", "").replace("out ", "").strip()
    if t in prim: return prim[t]
    if t.endswith("[]") or "<" in t: return "longlong"
    k = kinds.get(t)
    if k == "enum": return "int"
    if k == "struct" or t.endswith("?"): return None
    return "longlong"
sigs, funcs = [], []
for full, t in m.items():
    for me in t["methods"]:
        if me["addr"] <= 0: continue
        funcs.append("%x\t%s\t%s" % (me["addr"], full, me["name"]))
        ps = [] if me["static"] else [("longlong", "this")]
        ok = True
        for ty, name in me["params"]:
            g = gtype(ty)
            if g is None or g == "void": ok = False; break
            ps.append((g, name))
        r = gtype(me["ret"])
        if not ok or r is None: continue
        ps.append(("longlong", "method"))
        sigs.append("%x\t%s\t%s" % (me["addr"], r, ",".join("%s %s" % p for p in ps)))
open(os.path.join(out, "sigs.tsv"), "w").write("\n".join(sigs) + "\n")
open(os.path.join(out, "funcs.tsv"), "w").write("\n".join(funcs) + "\n")
print("methods %d, signatures %d, functions to decompile %d" % (len(ms), len(sigs), len(funcs)))
