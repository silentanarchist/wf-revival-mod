#!/usr/bin/env python3
"""Match every Android method to the same method in the PC build, by type, name and parameter types.
Usage: match_pc.py <android model.json> <pc model.json> <out pc-addr.json>
Writes {android address hex: PC RVA (int)}; assemble.py prints both in each method's header. Compiler-generated
lambdas (`<Foo>b__N_M`) get different numbers in the two builds, so inside one type they are matched by their
order when the names differ. Prints what did not match (the platform services, by and large)."""
import json, re, sys

A = json.load(open(sys.argv[1]))["types"]
P = json.load(open(sys.argv[2]))["types"]

def key(m):
    return (m["name"], m["ret"], tuple(ty for ty, _ in m["params"]))      # the return type tells op_Implicit overloads apart

LAMBDA = re.compile(r"^(<\w*>b__)\d+_(\d+)$")
out, unmatched = {}, []
for full, t in A.items():
    pt = P.get(full)
    if not pt:
        unmatched += [(full, m["name"]) for m in t["methods"] if m["addr"] > 0]
        continue
    pk = {}
    for m in pt["methods"]:
        if m["addr"] > 0: pk.setdefault(key(m), m["addr"])
    left = []
    for m in t["methods"]:
        if m["addr"] <= 0: continue
        k = key(m)
        if k in pk: out["%x" % m["addr"]] = pk[k]          # kept: several Android methods with one signature can share the PC code
        else: left.append(m)
    # lambdas renumbered between the builds: pair the leftovers with the same prefix and parameter types, in order
    for m in left:
        lm = LAMBDA.match(m["name"])
        if not lm: unmatched.append((full, m["name"])); continue
        cands = sorted(k for k in pk if LAMBDA.match(k[0]) and LAMBDA.match(k[0]).group(1) == lm.group(1) and k[1:] == key(m)[1:])
        if cands: out["%x" % m["addr"]] = pk.pop(cands[0])
        else: unmatched.append((full, m["name"]))

json.dump(out, open(sys.argv[3], "w"), separators=(",", ":"))
print("matched %d, Android-only %d" % (len(out), len(unmatched)))
by_type = {}
for full, n in unmatched: by_type.setdefault(full, []).append(n)
for full, ns in sorted(by_type.items(), key=lambda x: -len(x[1]))[:15]:
    print("  %s: %d (%s%s)" % (full, len(ns), ", ".join(ns[:4]), ", ..." if len(ns) > 4 else ""))
