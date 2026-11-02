#!/usr/bin/env python3
"""Group the readable functions of some folders into batch files for the naming agents.
Usage: make_batches.py <game-code dir> <readable dir> <batch dir> <folder prefix>... (e.g. Battle/ Cards/ Decks/)"""
import os, re, sys
tree, rd, out = sys.argv[1:4]; prefixes = sys.argv[4:]
os.makedirs(out, exist_ok=True)
rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(tree, "methods.tsv"))][1:]
items = []
for addr, cls, meth, path in rows:
    if not any(path.startswith(p) for p in prefixes): continue
    a = "%x" % int(addr, 16); p = os.path.join(rd, a + ".cs")
    if not os.path.exists(p): continue
    t = open(p, encoding="utf-8").read()
    if t.startswith("// "): continue                                  # already named
    if not re.search(r"\b[a-z]{1,3}Var\d|Stack_|\*\(\w", t): continue  # nothing to name
    items.append((a, cls, meth, t))
batches, cur, size = [], [], 0
for it in items:
    if cur and size + len(it[3]) > 130000: batches.append(cur); cur, size = [], 0
    cur.append(it); size += len(it[3])
if cur: batches.append(cur)
for i, b in enumerate(batches):
    with open(os.path.join(out, "batch%03d.txt" % (i + 1)), "w", encoding="utf-8") as f:
        for a, cls, meth, t in b: f.write("=== FUNCTION %s  %s::%s\n%s\n" % (a, cls, meth, t))
print("%d functions in %d batches" % (len(items), len(batches)))
