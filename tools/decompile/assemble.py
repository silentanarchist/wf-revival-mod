#!/usr/bin/env python3
"""Assemble the per-function readable output into one file per top-level type, in a project tree.
Usage: assemble.py <readable dir> <out root> [folders.json] [pc readable dir]
The pc readable dir (readable_pc.py output, files named by PC code address) holds methods that exist only in the
Windows build; each goes into its type after the Android methods, marked "Windows only"."""
import json, os, re, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = json.load(open(os.path.join(HERE, "model.json")))["types"]
PC_ADDR = json.load(open(os.path.join(HERE, "pc-addr.json"))) if os.path.exists(os.path.join(HERE, "pc-addr.json")) else {}

def addr_note(addr):
    """The method's code address on Android and, when the same method exists in the Windows build, there too
    (RVA inside GameAssembly.dll)."""
    pc = PC_ADDR.get("%x" % addr)
    return "// android 0x%X · pc %s" % (addr, ("0x%X" % pc) if pc else "not in the PC build")
rd_dir, out_root = sys.argv[1], sys.argv[2]
folders = json.load(open(sys.argv[3])) if len(sys.argv) > 3 and os.path.exists(sys.argv[3]) else {}
pc_dir = sys.argv[4] if len(sys.argv) > 4 else None
PC_ONLY = collections.defaultdict(list)      # type key -> [(pc address, method)] for methods only the PC build has
if pc_dir:
    PC_GAME = json.load(open(os.path.join(HERE, "pc-model", "model.json")))["types"]
    have = {f[:-3] for f in os.listdir(pc_dir) if f.endswith(".cs")}
    for full, t in PC_GAME.items():
        for m in t["methods"]:
            if m["addr"] > 0 and "%x" % (m["addr"] + 0x180000000) in have:
                PC_ONLY[full].append((m["addr"] + 0x180000000, m))

def short_type(t):
    return re.sub(r"\b(System\.Collections\.Generic|System\.Collections|UnityEngine\.UI|UnityEngine|System|CodeStage\.AntiCheat\.ObscuredTypes|Everguild\.LiveOps|Everguild)\.", "", t)

def safe_name(n):
    m = re.match(r"<>f__AnonymousType(\d+)<", n)
    if m: return "AnonymousType" + m.group(1)
    n = re.sub(r"<([^<>]*)>", lambda m: "_" + m.group(1).replace(",", "_") if m.group(1) else "", n)
    n = re.sub(r"[^\w.]", "_", n)
    n = n.strip("_") or "_"
    return n[:80]

def nested_kind(name):
    """what a compiler-generated nested type is"""
    m = re.match(r"<(\w+)>d__\d+$", name)
    if m: return "coroutine / iterator state machine for %s()" % m.group(1)
    m = re.match(r"<>c__DisplayClass(\d+)_\d+$", name)
    if m: return "closure (captured locals of a lambda in method #%s)" % m.group(1)
    if name == "<>c": return "static lambda holder"
    m = re.match(r"<(\w+)>e__FixedBuffer", name)
    if m: return "fixed buffer"
    if name.startswith("<"): return "compiler-generated"
    return None

def method_header_line(t, m):
    mods = m.get("mods", "")
    name = m["name"]
    if name == ".ctor": name = t["name"].split("<")[0]
    elif name == ".cctor": name = "static " + t["name"].split("<")[0]
    ret = "" if m["name"] in (".ctor", ".cctor") else short_type(m["ret"]) + " "
    ps = ", ".join("%s %s" % (short_type(ty), n) for ty, n in m["params"])
    return "%s%s%s(%s)" % (mods + " " if mods else "", ret, name, ps)

def render_type(t, depth, full):
    ind = "    " * depth
    out = []
    kind = t["kind"]
    nk = nested_kind(t["name"].split(".")[-1])
    if nk: out.append(ind + "// " + nk)
    base = short_type(t["base"]) if t["base"] else ""
    head = "%s%s %s%s" % (ind, kind, t["name"].split(".")[-1], (" : " + base) if base else "")
    out.append(head)
    out.append(ind + "{")
    inner = ind + "    "
    if kind == "enum":
        for v, n in sorted(((int(k), v) for k, v in t["enum"].items())):
            out.append("%s%s = %d," % (inner, n, v))
        out.append(ind + "}")
        return out
    # fields
    inst = [f for f in t["fields"] if not f["static"] and not f["const"]]
    stat = [f for f in t["fields"] if f["static"] and not f["const"]]
    const = [f for f in t["fields"] if f["const"]]
    if inst:
        out.append(inner + "// fields (offset in the object)")
        for f in inst:
            out.append("%s%s %s %s;%s" % (inner, ("/* 0x%x */" % f["offset"]).ljust(12), short_type(f["type"]), f["name"], ""))
    if stat:
        out.append(inner + "// static fields")
        for f in stat:
            out.append("%s%s static %s %s;" % (inner, ("/* 0x%x */" % f["offset"]).ljust(12), short_type(f["type"]), f["name"]))
    if const:
        for f in const:
            out.append("%sconst %s %s = %s;" % (inner, short_type(f["type"]), f["name"], f.get("value")))
    if t["props"]:
        out.append(inner + "// properties")
        for p in t["props"]:
            acc = (" get;" if p["get"] else "") + (" set;" if p["set"] else "")
            out.append("%s%s %s {%s }" % (inner, short_type(p["type"]), p["name"], acc))
    # methods
    for m in t["methods"]:
        out.append("")
        if m["addr"] <= 0:
            out.append(inner + method_header_line(t, m) + ";   // no code (abstract, generic definition or stripped)")
            continue
        path = os.path.join(rd_dir, "%x.cs" % m["addr"])
        if not os.path.exists(path):
            out.append(inner + method_header_line(t, m) + "   " + addr_note(m["addr"]) + ": not decompiled")
            continue
        body = open(path, encoding="utf-8").read().rstrip("\n").split("\n")
        if body and body[0].startswith("// "):
            if body[0] != "// (named)": out.append(inner + body[0])
            body = body[1:]
        # first line is the header from the readability pass; replace it with the modifiers form
        hdr = method_header_line(t, m) + "   " + addr_note(m["addr"])
        out.append(inner + hdr)
        out.append(inner + "{")
        for l in body[1:]:
            out.append((inner + l) if l.strip() else "")
        out.append(inner + "}")
    # methods only the Windows build has (the Android build dropped them: mouse, keyboard, PC skin)
    if PC_ONLY.get(full):
        out.append("")
        out.append(inner + "// ---- Windows only: these methods are not in the Android build")
        seen = {}
        for pc_addr, m in PC_ONLY[full]:
            out.append("")
            if pc_addr in seen:      # identical code folded by the compiler
                out.append(inner + method_header_line(t, m) + "   // pc 0x%X · not in the Android build  (Windows only): same code as %s() above" % (pc_addr - 0x180000000, seen[pc_addr]))
                continue
            seen[pc_addr] = m["name"]
            body = open(os.path.join(pc_dir, "%x.cs" % pc_addr), encoding="utf-8").read().rstrip("\n").split("\n")
            if body and body[0].startswith("// "):
                if body[0] != "// (named)": out.append(inner + body[0])
                body = body[1:]
            out.append(inner + method_header_line(t, m) + "   // pc 0x%X · not in the Android build  (Windows only)" % (pc_addr - 0x180000000))
            out.append(inner + "{")
            for l in body[1:]:
                out.append((inner + l) if l.strip() else "")
            out.append(inner + "}")
    # nested types
    for nk_, nt in sorted(NESTED.get(full, []), key=lambda x: x[1]["tdi"]):
        out.append("")
        out.extend(render_type(nt, depth + 1, nk_))
    out.append(ind + "}")
    return out

# nested type index: "Outer.Inner" -> under "Outer"
NESTED = collections.defaultdict(list)
TOP = {}
for full, t in GAME.items():
    if "." not in t["name"]: TOP[full] = t
BY_SHORT = collections.defaultdict(list)
for full, t in GAME.items(): BY_SHORT[t["name"]].append(full)
def outer_key(t):
    """the model key of a nested type's outer type (dump.cs lists nested types without their namespace)"""
    outer = t["name"].rsplit(".", 1)[0]
    cands = BY_SHORT.get(outer, [])
    if (t["ns"] + "." if t["ns"] else "") + outer in cands: return (t["ns"] + "." if t["ns"] else "") + outer
    return cands[0] if cands else outer
for full, t in GAME.items():
    if "." in t["name"]: NESTED[outer_key(t)].append((full, t))

def folder_for(full, t):
    if t["image"] == "Assembly-CSharp-firstpass.dll":
        ns = t["ns"] or "Misc"
        return "ThirdParty/" + ns.replace(".", "/")
    f = folders.get(full) or folders.get(t["name"])
    if f: return f
    if t["name"].startswith("<>f__AnonymousType") or t["name"].startswith("<PrivateImplementationDetails>"): return "Generated"
    base = t["name"].split("<")[0]
    for k, v in folders.items():
        if k.split("<")[0] == base: return v
    if t["ns"]: return "Other/" + t["ns"].replace(".", "/")
    return "Other"

index = []
written = 0
for full, t in sorted(TOP.items()):
    folder = folder_for(full, t)
    d = os.path.join(out_root, folder)
    os.makedirs(d, exist_ok=True)
    fname = safe_name(t["name"]) + ".cs"
    path = os.path.join(d, fname)
    if os.path.exists(path):   # same name in two namespaces
        fname = safe_name((t["ns"] + "." if t["ns"] else "") + t["name"]) + ".cs"
        path = os.path.join(d, fname)
    lines = ["// %s   (%s%s)" % (full, t["image"].replace(".dll", ""), (", namespace " + t["ns"]) if t["ns"] else ""),
             "// Decompiled pseudo-code from the game's compiled code (libil2cpp.so, game 1.35.0) - for reading and writing hooks, not for compiling.",
             "// Field offsets are the real in-memory offsets. See README.md for how to read this.", ""]
    if t["ns"]: lines.append("namespace %s;" % t["ns"]); lines.append("")
    lines.extend(render_type(t, 0, full))
    open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    written += 1
    nmeth = sum(1 for m in t["methods"] if m["addr"] > 0) + sum(1 for _, nt in NESTED.get(full, []) for m in nt["methods"] if m["addr"] > 0)
    index.append((folder + "/" + fname, full, t["kind"], nmeth))
    for nk_, nt in NESTED.get(full, []):
        for m in nt["methods"]:
            if m["addr"] > 0: pass

# index files
index.sort()
with open(os.path.join(out_root, "INDEX.md"), "w") as f:
    f.write("# Index of types\n\nFile | Type | Kind | Methods with code\n--- | --- | --- | ---\n")
    for p, full, k, n in index:
        f.write("%s | %s | %s | %d\n" % (p, full, k, n))
for cand in ("tree-README.md", "README.md"):            # the guide to reading the output (tree-README.md in the tools repo)
    if os.path.exists(os.path.join(HERE, cand)):
        import shutil; shutil.copyfile(os.path.join(HERE, cand), os.path.join(out_root, "README.md")); break
with open(os.path.join(out_root, "methods.tsv"), "w") as f:
    f.write("address\ttype\tmethod\tfile\n")
    fileof = {full: p for p, full, _, _ in index}
    for full, t in GAME.items():
        top = full
        while top in GAME and "." in GAME[top]["name"]: top = outer_key(GAME[top])
        for m in t["methods"]:
            if m["addr"] > 0: f.write("0x%X\t%s\t%s\t%s\n" % (m["addr"], full, m["name"], fileof.get(top, "?")))
print("wrote %d files" % written)
