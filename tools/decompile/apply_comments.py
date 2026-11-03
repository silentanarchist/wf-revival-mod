#!/usr/bin/env python3
"""Apply AI-written explanatory comments: <maps dir>/*.json = {addr: {"summary": "...", "comments": [[line, "text"], ...]}}
A comment is inserted as its own `// text` line just before the numbered line (1-based, as in the batch file), with
that line's indentation; the summary replaces the function's first `// ` line when given.
Usage: apply_comments.py <readable dir> <maps dir> [done list]   (the done list records the functions already
commented so a second run does not comment them twice)"""
import glob, json, os, re, sys

RD, MAPS = sys.argv[1], sys.argv[2]
DONE = sys.argv[3] if len(sys.argv) > 3 else os.path.join(MAPS, "applied.txt")
done = set(open(DONE).read().split()) if os.path.exists(DONE) else set()
funcs = comments = 0
with open(DONE, "a") as donef:
    for path in sorted(glob.glob(os.path.join(MAPS, "*.json"))):
        try: data = json.load(open(path, encoding="utf-8"))
        except Exception as e: print("bad json", path, e); continue
        for addr, info in data.items():
            addr = addr.lower()
            if addr in done or not isinstance(info, dict): continue
            p = os.path.join(RD, addr + ".cs")
            if not os.path.exists(p): continue
            lines = open(p, encoding="utf-8").read().rstrip("\n").split("\n")
            if not lines[0].startswith("// "): continue
            s = (info.get("summary") or "").strip().replace("\n", " ")
            if s: lines[0] = "// " + s
            ins = []
            for item in info.get("comments") or []:
                try: n, text = int(item[0]), str(item[1]).strip().replace("\n", " ")
                except Exception: continue
                if n < 3 or n > len(lines) or not text: continue       # never above the summary/header
                ins.append((n, text))
            for n, text in sorted(ins, key=lambda x: -x[0]):           # bottom-up so numbers stay valid
                indent = re.match(r"[ \t]*", lines[n - 1]).group(0)
                if lines[n - 1].strip() == "": indent = re.match(r"[ \t]*", lines[n] if n < len(lines) else "").group(0)
                lines.insert(n - 1, indent + "// " + text)
                comments += 1
            open(p, "w", encoding="utf-8").write("\n".join(lines) + "\n")
            donef.write(addr + "\n"); done.add(addr); funcs += 1
print("functions %d, comments %d" % (funcs, comments))
