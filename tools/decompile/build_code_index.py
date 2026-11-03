#!/usr/bin/env python3
"""The data for the website's developer code browser (/#code).
Usage: build_code_index.py <game-code dir> <xref.tsv> <out dir> <folder>...   (e.g. Battle Cards Decks)

Writes <out dir>/files/<path> (the served files, unchanged) and <out dir>/index.json:
  files    [[path, lines]]
  types    {type key: [display name, path, line]}           every type in the served files
  methods  {method id: [display "Type.Method", path, line, end line]}   every game method (path "" when not served)
  fields   {field id: [display "Type.field", path, line]}    fields declared in served files
  symbols  [[name, kind, owner, path, line]]                 for the search (kind: type|method|field|enum|property)
  xref     [[caller method id, kind c|r|w, target id]]       where caller or target is in a served file
Method ids are Android code addresses in lower-case hex, "pc:<rva>" for Windows-only methods, or "Type::Name" for
methods without code.
Field ids are "Type.field" with the type's full key (namespace included)."""
import json, os, re, shutil, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from readable import clean_field          # the same field names the readable code uses

src, xref_path, out = sys.argv[1:4]
folders = [f.strip("/") + "/" for f in sys.argv[4:]]

rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(src, "methods.tsv"), encoding="utf-8")][1:]
methods = {}                                   # id -> [display, path, line, end]
for addr, typ, meth, path in rows:
    methods["%x" % int(addr, 16)] = [typ.split(".")[-1] if "." in typ and "<" not in typ else typ, "", 0, 0, meth]
for v in methods.values():                     # display: short type + method name
    v[0] = v[0] + "." + v.pop()

served = []
for root, _, fs in os.walk(src):
    for f in fs:
        rel = os.path.relpath(os.path.join(root, f), src).replace(os.sep, "/")
        if rel.endswith(".cs") and any(rel.startswith(p) for p in folders):
            served.append(rel)
served.sort()

TYPE = re.compile(r"^( *)(class|struct|enum|interface) (\S+)")
METHOD = re.compile(r"^( *)(?:[\w<>\[\],.? ]+ )?([\w<>.`$]+)\((.*)\)   // android 0x([0-9A-F]+) · pc .*$")
PCONLY = re.compile(r"^( *)(?:[\w<>\[\],.? ]+ )?([\w<>.`$]+)\((.*)\)   // pc 0x([0-9A-F]+) · not in the Android build  \(Windows only\)(: same code.*)?$")
NOCODE = re.compile(r"^( *)(?:[\w<>\[\],.? ]+ )?([\w<>.`$]+)\((.*)\);   // no code")
FIELD = re.compile(r"^( *)/\* 0x[0-9a-f]+ \*/ +(static )?(.+?) ([\w<>$@`.]+);(?:   //.*)?$")
ENUMV = re.compile(r"^( *)(\w+) = (-?\d+),$")
PROP = re.compile(r"^( *)(.+?) (\w+) \{( get;)?( set;)? \}$")

types, fields, symbols = {}, {}, []
files = []
for rel in served:
    text = open(os.path.join(src, rel), encoding="utf-8").read()
    lines = text.split("\n")
    files.append([rel, len(lines)])
    ns = ""
    stack = []                                 # (indent, type key, short name, kind)
    open_method = None                         # (id, indent)
    for i, line in enumerate(lines, 1):
        if line.startswith("namespace ") and line.endswith(";"):
            ns = line[10:-1].strip()
            continue
        if open_method is not None:
            if line == open_method[1] + "}":
                methods[open_method[0]][3] = i
                open_method = None
            continue
        m = TYPE.match(line)
        if m:
            ind = len(m.group(1))
            while stack and stack[-1][0] >= ind: stack.pop()
            name = m.group(3)
            key = (stack[-1][1] + "." + name) if stack else ((ns + "." if ns else "") + name)
            stack.append((ind, key, name, m.group(2)))
            types[key] = [name, rel, i]
            symbols.append([name, "type", stack[-2][2] if len(stack) > 1 else ns, rel, i])
            continue
        if not stack: continue
        owner_key, owner = stack[-1][1], stack[-1][2]
        m = METHOD.match(line)
        if m:
            mid = m.group(4).lower()
            if mid in methods:
                methods[mid][1:4] = [rel, i, i]
            else:
                methods[mid] = [owner + "." + m.group(2), rel, i, i]
            open_method = (mid, m.group(1))
            symbols.append([m.group(2), "method", owner, rel, i])
            continue
        m = PCONLY.match(line)
        if m:                                  # Windows-only method: id "pc:<address>"
            mid = "pc:" + m.group(4).lower()
            methods[mid] = [owner + "." + m.group(2), rel, i, i]
            if not m.group(5): open_method = (mid, m.group(1))
            symbols.append([m.group(2), "method", owner, rel, i])
            continue
        m = NOCODE.match(line)
        if m:
            mid = owner_key + "::" + m.group(2)
            methods[mid] = [owner + "." + m.group(2), rel, i, i]
            symbols.append([m.group(2), "method", owner, rel, i])
            continue
        m = FIELD.match(line)
        if m:
            fname = clean_field(m.group(4))
            fields[owner_key + "." + fname] = [owner + "." + fname, rel, i]
            symbols.append([fname, "field", owner, rel, i])
            continue
        if stack[-1][3] == "enum":
            m = ENUMV.match(line)
            if m:
                symbols.append([m.group(2), "enum", owner, rel, i])
            continue
        m = PROP.match(line)
        if m and (m.group(4) or m.group(5)):
            symbols.append([m.group(3), "property", owner, rel, i])

# cross-references: keep the edges that touch a served method or a served field
served_methods = {k for k, v in methods.items() if v[1]}
xref = []
for l in open(xref_path, encoding="utf-8"):
    caller, kind, target = l.rstrip("\n").split("\t")
    if caller in served_methods or (kind == "c" and target in served_methods) or (kind != "c" and target in fields):
        xref.append([caller, kind, target])

os.makedirs(out, exist_ok=True)
fdir = os.path.join(out, "files")
if os.path.isdir(fdir): shutil.rmtree(fdir)
for rel in served:
    dst = os.path.join(fdir, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(os.path.join(src, rel), dst)
# methods not in served files and never referenced from them are left out to keep the index small
used = {x[0] for x in xref} | {x[2] for x in xref if x[1] == "c"}
methods = {k: v for k, v in methods.items() if v[1] or k in used}
json.dump({"files": files, "types": types, "methods": methods, "fields": fields, "symbols": symbols, "xref": xref,
           "folders": [f.rstrip("/") for f in folders]},
          open(os.path.join(out, "index.json"), "w", encoding="utf-8"), separators=(",", ":"))
print("%d files, %d types, %d methods (%d served), %d fields, %d symbols, %d cross-references" %
      (len(files), len(types), len(methods), sum(1 for v in methods.values() if v[1]), len(fields), len(symbols), len(xref)))
