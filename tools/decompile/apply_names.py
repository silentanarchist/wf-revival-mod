#!/usr/bin/env python3
"""Apply AI-chosen names: <maps dir>/*.json = {addr: {"summary": "...", "renames": {"old": "new", ...}}}
Only decompiler-made names may be renamed; a new name must be a valid identifier not already used in the function.
Usage: apply_names.py <readable dir> <maps dir>"""
import json, os, re, sys, glob

RD = sys.argv[1]
MAPS = sys.argv[2]
OLD = re.compile(r"^(?:[a-z]{1,4}Var\d+|[a-z]*Stack_[0-9a-f]+|local_[0-9a-f]+|in_\w+|unaff_\w+|extraout_\w+|this_\d+|param_\d+|register0x\w+|[a-z]{1,4}Var\d+_x1|[a-z]{1,4}Var\d+_part\d+)(?:_\d+)?$")
RESERVED = {"this", "method", "null", "true", "false", "new", "return", "if", "else", "while", "for", "do", "goto", "throw", "switch", "case", "break", "continue", "int", "bool", "string", "object", "long", "float", "void", "var"}
applied = done = 0
for path in sorted(glob.glob(os.path.join(MAPS, "*.json"))):
    try: data = json.load(open(path))
    except Exception as e: print("bad json", path, e); continue
    for addr, info in data.items():
        p = os.path.join(RD, addr.lower() + ".cs")
        if not os.path.exists(p): continue
        t = open(p, encoding="utf-8").read()
        if t.startswith("// "): continue    # already done
        words = set(re.findall(r"\b[A-Za-z_]\w*\b", t))
        ren = info.get("renames", {}) if isinstance(info, dict) else {}
        used = set()
        pairs = []
        for old, new in ren.items():
            if not OLD.match(old) or old not in words: continue
            if not re.fullmatch(r"[a-z_][A-Za-z0-9_]*", new or "") or new in RESERVED or new in words or new in used: continue
            used.add(new); pairs.append((old, new))
        if pairs:
            rx = re.compile(r"\b(" + "|".join(re.escape(o) for o, _ in pairs) + r")\b")
            m = dict(pairs)
            lines = t.split("\n")
            lines = [lines[0]] + [rx.sub(lambda mm: m[mm.group(1)], l) for l in lines[1:]]
            t = "\n".join(lines); applied += len(pairs)
        s = (info.get("summary") or "").strip().replace("\n", " ") if isinstance(info, dict) else ""
        if s: t = "// " + s + "\n" + t
        else: t = "// (named)\n" + t
        open(p, "w", encoding="utf-8").write(t); done += 1
print("functions %d, renames %d" % (done, applied))
