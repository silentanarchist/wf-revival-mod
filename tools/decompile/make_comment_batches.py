#!/usr/bin/env python3
"""Batch the AI-named functions of some folders for the commenting agents, with numbered lines.
Usage: make_comment_batches.py <game-code dir> <readable dir> <batch dir> <folder prefix>...
Each function is written as `=== FUNCTION <addr>  <Class>::<Method>` followed by its readable text with `N| `
line numbers (1 = the summary line, 2 = the method header); the agents answer with line numbers."""
import os, sys
tree, rd, out = sys.argv[1:4]; prefixes = sys.argv[4:]
os.makedirs(out, exist_ok=True)
rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(tree, "methods.tsv"))][1:]
items = []
for addr, cls, meth, path in rows:
    if not any(path.startswith(p) for p in prefixes): continue
    a = "%x" % int(addr, 16); p = os.path.join(rd, a + ".cs")
    if not os.path.exists(p): continue
    t = open(p, encoding="utf-8").read().rstrip("\n")
    if not t.startswith("// "): continue                               # only the AI-named functions
    lines = t.split("\n")
    if len(lines) < 5: continue                                        # one-liners: nothing to explain
    body = "\n".join("%d| %s" % (i + 1, l) for i, l in enumerate(lines))
    items.append((a, cls, meth, body))
batches, cur, size = [], [], 0
for it in items:
    if cur and size + len(it[3]) > 120000: batches.append(cur); cur, size = [], 0
    cur.append(it); size += len(it[3])
if cur: batches.append(cur)
for i, b in enumerate(batches):
    with open(os.path.join(out, "cbatch%03d.txt" % (i + 1)), "w", encoding="utf-8") as f:
        for a, cls, meth, t in b: f.write("=== FUNCTION %s  %s::%s\n%s\n" % (a, cls, meth, t))
print("%d functions in %d batches" % (len(items), len(batches)))
