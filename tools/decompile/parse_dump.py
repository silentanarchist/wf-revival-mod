#!/usr/bin/env python3
"""Parse an Il2CppDumper dump.cs into a JSON model: types (fields with offsets, enum values, methods with addresses,
parameters) for the images we care about. Usage: parse_dump.py dump.cs out.json [image names...]"""
import re, sys, json

src, out = sys.argv[1], sys.argv[2]
want_images = set(sys.argv[3:]) or None

images = {}   # index -> name ; types belong to an image by TypeDefIndex ranges
img_starts = []  # (first typedef index, image name)
for line in open(src, encoding="utf-8", errors="replace"):
    m = re.match(r"// Image (\d+): (\S+) - (\d+)", line)
    if m:
        img_starts.append((int(m.group(3)), m.group(2)))
    elif line.startswith("// Namespace"):
        break
img_starts.sort()

def image_of(tdi):
    name = None
    for start, n in img_starts:
        if tdi >= start: name = n
        else: break
    return name

types = {}
cur = None
ns = ""
section = None
pending_rva = None
pending_slot = -1
re_type = re.compile(r"^(?:\[.*?\]\s*)*((?:public|private|internal|protected|static|sealed|abstract|readonly|\s)*)(class|struct|enum|interface)\s+(.+?)(?:\s*:\s*([^/]+?))?\s*// TypeDefIndex: (\d+)")
def norm_generic(s):
    """'Dictionary<TKey, TValue>' -> 'Dictionary<TKey,TValue>' (spaces inside <> removed; top-level ', ' kept)"""
    out, depth = [], 0
    for ch in s:
        if ch == "<": depth += 1
        elif ch == ">": depth -= 1
        if ch == " " and depth > 0: continue
        out.append(ch)
    return "".join(out)
re_field = re.compile(r"^\t((?:public|private|internal|protected|static|readonly|const|\s)*)([^\s].*?)\s+([A-Za-z_<>$@][\w<>$@.]*)(?:\s*=\s*(.+?))?;\s*// (0x[0-9A-Fa-f]+|-1)")
re_enum_member = re.compile(r"^\tpublic const \S+ (\S+) = (-?\d+);")
re_rva = re.compile(r"^\t// RVA: (0x[0-9A-Fa-f]+|-1) Offset: (?:0x[0-9A-Fa-f]+|-1)(?: VA: (0x[0-9A-Fa-f]+))?(?: Slot: (\d+))?")
re_method = re.compile(r"^\t((?:public|private|internal|protected|static|virtual|override|abstract|sealed|extern|new|unsafe|\s)*)(.+?)\s+([^\s(]+)\((.*)\)\s*(?:\{\s*\}|;)")
re_ctor = re.compile(r"^\t((?:public|private|internal|protected|static|\s)*)(\.c?ctor)\((.*)\)\s*(?:\{\s*\}|;)")
re_prop = re.compile(r"^\t((?:public|private|internal|protected|static|virtual|override|abstract|sealed|new|\s)*)(.+?)\s+([^\s{]+)\s*\{\s*(get;)?\s*(set;)?\s*\}")

def split_params(s):
    out, depth, cur_ = [], 0, ""
    for ch in s:
        if ch == "<": depth += 1
        elif ch == ">": depth -= 1
        if ch == "," and depth == 0:
            out.append(cur_.strip()); cur_ = ""
        else: cur_ += ch
    if cur_.strip(): out.append(cur_.strip())
    params = []
    for p in out:
        p = re.sub(r"^\[.*?\]\s*", "", p)
        p = re.sub(r"^(params|ref|out|in|this)\s+", lambda m: "", p)
        m = re.match(r"(.+?)\s+(\S+?)(?:\s*=\s*.+)?$", p)
        if m: params.append([m.group(1), m.group(2)])
        else: params.append([p, "arg%d" % len(params)])
    return params

for raw in open(src, encoding="utf-8", errors="replace"):
    line = raw.rstrip("\n")
    if line.startswith("// Namespace: "):
        ns = line[len("// Namespace: "):].strip()
        cur = None
        continue
    if line.startswith("\t") and "(" in line and "<" in line.split("(")[0]:
        line = norm_generic(line.split("(")[0]) + "(" + "(".join(line.split("(")[1:])
    m = re_type.match(line)
    if m and not line.startswith("\t"):
        mods, kind, name, base, tdi = m.groups()
        name = norm_generic(name); base = norm_generic(base or "")
        tdi = int(tdi)
        img = image_of(tdi)
        full = (ns + "." if ns else "") + name
        cur = {"name": name, "ns": ns, "kind": kind, "base": (base or "").strip(), "tdi": tdi, "image": img, "mods": " ".join(mods.split()),
               "static": "static" in mods, "fields": [], "methods": [], "enum": {}, "props": []}
        types[full] = cur
        section = None
        continue
    if cur is None: continue
    if want_images and cur["image"] not in want_images: continue
    s = line.strip()
    if s in ("// Fields", "// Properties", "// Methods"):
        section = s[3:]; continue
    if section == "Fields":
        if cur["kind"] == "enum":
            m = re_enum_member.match(line)
            if m: cur["enum"][int(m.group(2))] = m.group(1)
            continue
        m = re_field.match(line)
        if m:
            mods, ftype, fname, value, off = m.groups()
            cur["fields"].append({"name": fname, "type": ftype.strip(), "offset": int(off, 16) if off != "-1" else -1, "mods": " ".join(mods.split()),
                                  "static": "static" in mods, "const": "const" in mods, "value": value})
    elif section == "Properties":
        m = re_prop.match(line)
        if m: cur["props"].append({"name": m.group(3), "type": m.group(2).strip(), "get": bool(m.group(4)), "set": bool(m.group(5))})
    elif section == "Methods":
        m = re_rva.match(line)
        if m:
            pending_rva = int(m.group(1), 16) if m.group(1) != "-1" else -1
            pending_slot = int(m.group(3)) if m.group(3) else -1
            continue
        if line.startswith("\t// ") or line.startswith("\t["): continue
        m = re_method.match(line) or None
        if m:
            mods, ret, name, params = m.groups()
            cur["methods"].append({"addr": pending_rva, "name": name, "ret": ret.strip(), "params": split_params(params), "slot": pending_slot, "mods": " ".join(mods.split()),
                                   "static": "static" in mods.split(), "virtual": any(k in mods.split() for k in ("virtual", "override", "abstract"))})
            pending_rva = None
            continue
        m = re_ctor.match(line)
        if m:
            mods, name, params = m.groups()
            cur["methods"].append({"addr": pending_rva, "name": name, "ret": "void", "params": split_params(params), "mods": " ".join(mods.split()),
                                   "static": "static" in mods.split() or name == ".cctor", "virtual": False})
            pending_rva = None

if want_images:
    types = {k: v for k, v in types.items() if v["image"] in want_images}
json.dump({"images": img_starts, "types": types}, open(out, "w"), indent=0)
nm = sum(1 for t in types.values() for m in t["methods"] if m["addr"] > 0)
print("types %d, methods with code %d" % (len(types), nm))
