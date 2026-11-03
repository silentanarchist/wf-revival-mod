#!/usr/bin/env python3
"""Turn Ghidra's raw IL2CPP decompiles into readable C#-flavoured pseudo-code.

Inputs: model.json (game types), model-all.json (every type: fields, methods), stringliteral.json, libil2cpp.so (icall names).
For each function: names `this` and the parameters, turns `*(long *)(x + 0x38)` into `x.fieldName` wherever x's class is
known (this, parameters, fields, call results - propagated to a fixed point), static fields, string literals, enum
names, `List.get_Item` -> `list[i]`, property getters/setters, and gives locals names after what they hold.
IL2CPP's own bookkeeping (metadata init, class init, write barriers, GC card marks) is removed.
"""
import re, json, sys, os, struct

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL_DIR = os.environ.get("READABLE_MODEL", HERE)      # another model (the PC build's): readable_pc.py
MODEL = json.load(open(os.path.join(MODEL_DIR, "model-all.json")))["types"]
GAME = json.load(open(os.path.join(MODEL_DIR, "model.json")))["types"]

# ---------------------------------------------------------------- name sanitising (how Ghidra prints the symbols)
def ghidra_c(name):
    """The symbol as it appears in Ghidra's C output: '.' and '$' and others become '_'."""
    return re.sub(r"[^A-Za-z0-9_<>,\[\]`]", "_", name)

# ---------------------------------------------------------------- type helpers
PRIM = {"int", "uint", "bool", "float", "double", "long", "ulong", "byte", "sbyte", "short", "ushort", "char", "void", "string", "object", "IntPtr", "UIntPtr"}

def strip_mod(t):
    t = t.strip()
    for m in ("ref ", "out ", "in ", "params "):
        if t.startswith(m): t = t[len(m):]
    return t

def generic_base(t):
    """'List<CardScript>' -> ('List', ['CardScript'])"""
    m = re.match(r"^([\w.]+)<(.*)>$", t)
    if not m: return t, []
    args, depth, cur = [], 0, ""
    for ch in m.group(2):
        if ch == "<": depth += 1
        elif ch == ">": depth -= 1
        if ch == "," and depth == 0: args.append(cur.strip()); cur = ""
        else: cur += ch
    if cur.strip(): args.append(cur.strip())
    return m.group(1), args

# index of types by short name and by (short name, arity) for generics
BY_NAME = {}
for full, t in MODEL.items():
    base, args = generic_base(t["name"])
    key = (base.split(".")[-1], len(args))
    BY_NAME.setdefault(key, full)
    BY_NAME.setdefault((t["name"], 0), full) if not args else None
    BY_NAME.setdefault((full, len(args)), full)
    BY_NAME.setdefault((full, 0), full)

def resolve_type(t, ns=None):
    """A C# type name (as written in dump.cs) -> (full model key or None, generic args list).
    ns: the namespace of the type the name was written in (dump.cs writes short names), tried first."""
    t = strip_mod(t)
    if t.endswith("[]") or t in PRIM: return None, []
    base, args = generic_base(t)
    key = None
    if ns:
        for cand in (ns + "." + base, ns.rsplit(".", 1)[0] + "." + base if "." in ns else None):
            if cand and (cand in MODEL or cand + "<T>" in MODEL):
                key = cand if cand in MODEL else None
                if key: break
    if key is None and base in ("Object", "UnityEngine.Object"): key = "UnityEngine.Object"
    if key is None and "." not in base and not args and base in MODEL and MODEL[base]["image"] == "Assembly-CSharp.dll": key = base
    if key is None and not args:
        for pre in ("UnityEngine.", "UnityEngine.UI.", "System.", "System.Collections.Generic.", "System.Collections."):
            if pre + base in MODEL: key = pre + base; break
    if key is None: key = BY_NAME.get((base, len(args))) or BY_NAME.get((base.split(".")[-1], len(args)))
    return key, args

# field layouts of the generic collections (dump.cs shows 0 for every field of a generic definition)
KNOWN_LAYOUT = {
    "System.Collections.Generic.List<T>": {0x10: ("_items", "T[]"), 0x18: ("_size", "int"), 0x1c: ("_version", "int")},
    "System.Collections.Generic.Dictionary<TKey,TValue>": {0x10: ("buckets", "int[]"), 0x18: ("entries", "Entry[]"), 0x20: ("count", "int"), 0x24: ("version", "int"), 0x28: ("freeList", "int"), 0x2c: ("freeCount", "int"), 0x30: ("comparer", "IEqualityComparer<TKey>"), 0x38: ("keys", "KeyCollection"), 0x40: ("values", "ValueCollection")},
    "System.Collections.Generic.HashSet<T>": {0x10: ("_buckets", "int[]"), 0x18: ("_slots", "Slot[]"), 0x20: ("_count", "int")},
    "System.Collections.Generic.Queue<T>": {0x10: ("_array", "T[]"), 0x18: ("_head", "int"), 0x1c: ("_tail", "int"), 0x20: ("_size", "int")},
    "System.Collections.Generic.Stack<T>": {0x10: ("_array", "T[]"), 0x18: ("_size", "int")},
    "System.Collections.Generic.KeyValuePair<TKey,TValue>": {0x0: ("key", "TKey"), 0x8: ("value", "TValue")},
    "System.Nullable<T>": {0x0: ("value", "T"), 0x8: ("hasValue", "bool")},
    "System.Delegate": {0x10: ("method_ptr", "IntPtr"), 0x18: ("invoke_impl", "IntPtr"), 0x20: ("m_target", "object"), 0x28: ("method", "IntPtr")},
}

def is_enum(t):
    k, _ = resolve_type(t)
    return k is not None and MODEL[k]["kind"] == "enum"

def enum_name(t, value):
    k, _ = resolve_type(t)
    if k is None: return None
    return MODEL[k]["enum"].get(str(value))

def is_value_type(t):
    k, _ = resolve_type(t)
    return k is not None and MODEL[k]["kind"] == "struct"

_fields_cache = {}
def clean_field(n):
    m = re.fullmatch(r"<(\w+)>k__BackingField", n)
    if m: return m.group(1)
    if n == "<>4__this": return "outer"
    if n == "<>1__state": return "state"
    if n == "<>2__current": return "current"
    if n == "<>t__builder": return "builder"
    if n == "<>9": return "instance"
    m = re.fullmatch(r"<(\w+)>5__\d+", n)
    if m: return m.group(1)
    m = re.fullmatch(r"<>s__(\d+)", n)
    if m: return "tmp" + m.group(1)
    m = re.fullmatch(r"<>u__(\d+)", n)
    if m: return "awaiter" + m.group(1)
    m = re.fullmatch(r"<>9__(\w+)", n)
    if m: return "cachedDelegate_" + m.group(1)
    m = re.fullmatch(r"<>(\w+)", n)
    if m: return "gen_" + m.group(1)
    return re.sub(r"[^\w.]", "_", n)

def fields_of(t):
    """instance fields of a class, including the base chain, keyed by offset -> (name, type); generics substituted"""
    t = strip_mod(t)
    if t in _fields_cache: return _fields_cache[t]
    res = {}
    k, args = resolve_type(t)
    chain = []
    seen = set()
    while k and k not in seen:
        seen.add(k)
        ty = MODEL[k]
        # generic parameter substitution
        gbase, gparams = generic_base(ty["name"])
        subst = dict(zip(gparams, args)) if args and len(gparams) == len(args) else {}
        fields = ty["fields"]
        if k in KNOWN_LAYOUT:
            fields = [{"name": n, "type": ft, "offset": o, "static": False, "const": False} for o, (n, ft) in KNOWN_LAYOUT[k].items()]
        for f in fields:
            if f["static"] or f["const"] or f["offset"] < 0: continue
            ftype = f["type"]
            if subst:
                ftype = re.sub(r"\b(" + "|".join(map(re.escape, subst)) + r")\b", lambda m: subst[m.group(1)], ftype)
            res.setdefault(f["offset"], (clean_field(f["name"]), ftype))
        base = ty["base"].split(",")[0].strip() if ty["base"] else ""
        if not base or base in ("ValueType", "Enum"): break
        if base.startswith("MulticastDelegate") or base == "Delegate": k, args = "System.Delegate", []; continue
        k, args = resolve_type(base, ty["ns"])
    _fields_cache[t] = res
    return res

def static_fields_of(t):
    k, _ = resolve_type(t)
    if not k: return {}
    return {f["offset"]: (clean_field(f["name"]), f["type"]) for f in MODEL[k]["fields"] if f["static"] and not f["const"] and f["offset"] >= 0}

def field_at(t, off):
    """-> (field expression suffix, field type) for an offset inside an object of type t, descending into struct fields"""
    fs = fields_of(t)
    if not fs: return None
    if off in fs: return fs[off]
    cands = [o for o in fs if o < off]
    if not cands: return None
    o = max(cands)
    name, ftype = fs[o]
    if is_value_type(ftype):
        k, _ = resolve_type(ftype)
        sub = {f["offset"]: (clean_field(f["name"]), f["type"]) for f in MODEL[k]["fields"] if not f["static"] and not f["const"] and f["offset"] >= 0}
        d = off - o
        if d in sub: return name + "." + sub[d][0], sub[d][1]
    return None

# ---------------------------------------------------------------- method index: C-printed callee name -> candidate methods
CALLEES = {}
TYPE_C = {}      # C-printed type name -> full key
for full, t in MODEL.items():
    gbase, gparams = generic_base(full)
    cfull = ghidra_c(full)
    TYPE_C[cfull] = full
    if gparams:   # List<T> is called as List<object>
        TYPE_C[ghidra_c(gbase + "<" + ",".join("object" for _ in gparams) + ">")] = full
    for m in t["methods"]:
        mname = m["name"].split("<")[0]
        CALLEES.setdefault((cfull if not gparams else ghidra_c(gbase + "<" + ",".join("object" for _ in gparams) + ">"), mname), []).append((full, m))
        if gparams: CALLEES.setdefault((cfull, mname), []).append((full, m))

def find_callee(cname):
    """'BattleCommsManager__get_matchData' -> (full type, method dict) or None"""
    cname = re.sub(r"___(c?ctor)$", r"__.\1", cname)
    # generic instantiations are called through the shared <object> code; value-type instantiations have their own
    cname = re.sub(r"<([^<>]*)>", lambda m: "<" + ",".join("object" for _ in m.group(1).split(",")) + ">", cname)
    i = cname.rfind("__")
    while i > 0:
        cls, meth = cname[:i], cname[i + 2:].split("<")[0]
        c = CALLEES.get((cls, meth))
        if c: return c
        # nested types are keyed without their namespace in dump.cs
        parts = cls.split("_")
        for k in range(1, len(parts)):
            c = CALLEES.get(("_".join(parts[k:]), meth))
            if c: return c
        i = cname.rfind("__", 0, i)
    return None

# ---------------------------------------------------------------- string literals and icall names
STRINGS = {}
def load_strings(path):
    for e in json.load(open(path)):
        v = e["value"]
        san = re.sub(r"[^A-Za-z0-9_]", "_", v)
        for name in ("str_" + san[:200], "str_" + san[:40]):
            STRINGS.setdefault(name, set()).add(v)

SO = None
SEGS = []
def load_so(path):
    global SO, SEGS
    SO = open(path, "rb").read()
    phoff = struct.unpack_from("<Q", SO, 0x20)[0]; phentsize, phnum = struct.unpack_from("<HH", SO, 0x36)
    for i in range(phnum):
        p_type, p_flags, p_offset, p_vaddr, p_paddr, p_filesz = struct.unpack_from("<IIQQQQ", SO, phoff + i * phentsize)
        if p_type == 1: SEGS.append((p_vaddr, p_vaddr + p_filesz, p_offset))
def so_string(va):
    """null-terminated string at a virtual address of libil2cpp.so"""
    if SO is None: return None
    off = None
    for lo, hi, o in SEGS:
        if lo <= va < hi: off = va - lo + o
    if off is None: return None
    start = off
    while start > 0 and SO[start - 1] != 0 and off - start < 300: start -= 1
    off = start
    end = SO.find(b"\0", off, off + 400)
    if end < 0: return None
    try: return SO[off:end].decode("utf-8")
    except: return None

def c_string(s):
    s = s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")
    s = "".join(ch if 32 <= ord(ch) < 127 or ord(ch) > 160 else "\\u%04x" % ord(ch) for ch in s)
    return '"' + s + '"'

FUNC_NAMES = {}
def load_script(path):
    for e in json.load(open(path))["ScriptMethod"]:
        n = e["Name"]
        i = n.find("$$")
        if i < 0: continue
        FUNC_NAMES["func_0x%08x" % e["Address"]] = ghidra_c(n[:i]) + "__" + re.sub(r"[^\w<>`]", "_", n[i + 2:])

# ---------------------------------------------------------------- IL2CPP helper functions still unnamed in Ghidra
HELPERS = {
    "func_0x02d2a3c4": "IDENTITY",         # metadata item (string literal / type) load: value is the argument
    "func_0x02d2a520": "throw",            # il2cpp_raise_exception(exception, method)
    "func_0x02d2a680": "il2cpp_new_exception",
    "func_0x02d2a664": "il2cpp_throw_error",
    "func_0x062e5da0": "memcpy", "func_0x062e5db0": "memcpy", "func_0x062e5dd0": "memset",
    "func_0x02d65600": "IsInst", "func_0x02d2a534": "IsInst",
    "func_0x02d2a538": "Box",
    "func_0x02d652cc": "il2cpp_init_class", "func_0x02d65328": "il2cpp_class_init",
    "func_0x02d2a36c": "icall",
    "func_0x02e1ada0": "GC_CARD_MARK",
    "func_0x02d2a52c": "il2cpp_throw_error",
    "func_0x02d2a53c": "Monitor.Enter", "func_0x02d2a9dc": "Monitor.Exit",
    "func_0x062e5d20": "__cxa_begin_catch", "func_0x062e5d30": "__cxa_end_catch", "func_0x062e5d70": "__stack_chk_fail",
    "func_0x062e5d40": "__cxa_allocate_exception", "func_0x062e5d50": "__cxa_throw", "func_0x062e5d80": "modf",
    "func_0x062e6160": "il2cpp_array_element_size", "func_0x062e5d60": "__cxa_get_exception_ptr",
    "func_0x02d2a654": "il2cpp_exception_unwind", "func_0x02d2a66c": "il2cpp_exception_unwind",
    "func_0x02960c44": "catch_handler", "func_0x02960c18": "catch_handler", "func_0x02960d20": "finally_block", "func_0x02960e68": "finally_block",
    "func_0x02960e58": "NULLCHECK", "func_0x0296645c": "CLASSINIT", "func_0x02d2a454": "BARRIER",
    "func_0x029658ec": "List.Add_inlined", "func_0x02d39638": "il2cpp_interface_offset", "func_0x02f99594": "EqualityComparer.get_Default",
    "func_0x02d64e2c": "Interlocked.CompareExchange", "func_0x02d2aa64": "String.Equals_inlined", "func_0x02d41a5c": "il2cpp_string_alloc",
}

# ---------------------------------------------------------------- the per-function rewrite
CAST_PTR = re.compile(r"\((?:undefined\d?|long|ulong|longlong|ulonglong|byte|char|code|uint|int|bool|ushort|short|float|double|void) \*+\)")
CAST_INT = re.compile(r"\((?:undefined8|undefined4|long|ulong|longlong|ulonglong)\)")

RESERVED = {"string", "object", "int", "uint", "bool", "float", "double", "long", "ulong", "byte", "char", "short", "new", "class", "struct", "enum", "null", "true", "false", "return", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "goto", "throw", "try", "catch", "finally", "static", "void", "var", "in", "out", "ref", "is", "as", "base", "this", "typeof", "event", "delegate", "lock", "fixed", "params", "operator", "override", "virtual", "public", "private", "internal", "protected", "readonly", "const", "using", "namespace", "interface", "abstract", "sealed", "foreach", "checked", "unchecked", "unsafe", "volatile", "extern", "implicit", "explicit", "sizeof", "stackalloc", "decimal", "sbyte", "ushort"}

def lower_first(s):
    return s[:1].lower() + s[1:] if s else s

class Func:
    def __init__(self, cls_full, method, raw):
        self.cls = cls_full
        self.m = method
        self.raw = raw
        self.types = {}      # variable / dotted path -> C# type
        self.xref = set()    # ("c", method id) calls, ("r"|"w", "Type.field") field reads and writes
        self.notes = []

    # ---- helpers on the type environment
    def type_of(self, path):
        """type of a dotted path like 'this.manager.playerHand' (None when unknown)"""
        if path in self.types: return self.types[path]
        if "." not in path: return None
        head, _, rest = path.rpartition(".")
        ht = self.type_of(head)
        if not ht: return None
        for off, (name, ftype) in fields_of(ht).items():
            if name == rest:
                self.types[path] = ftype; return ftype
        return None

    def run(self):
        text = self.raw
        lines = text.split("\n")
        # ---- header / signature / locals / body
        hdr = lines[0] if lines and lines[0].startswith("/* ----") else ""
        body_start = None
        sig_lines = []
        i = 1 if hdr else 0
        while i < len(lines) and lines[i].strip() == "": i += 1
        if i < len(lines) and lines[i].startswith("/* not decompiled") or i < len(lines) and lines[i].startswith("/* no function"):
            return self.emit_header() + "    " + lines[i].strip() + "\n"
        while i < len(lines) and lines[i].strip() != "{":
            sig_lines.append(lines[i]); i += 1
        sig = " ".join(l.strip() for l in sig_lines)
        i += 1
        decl, body = [], []
        in_decl = True
        while i < len(lines):
            l = lines[i]; i += 1
            if in_decl:
                if l.strip() == "": in_decl = False; continue
                if re.match(r"^\s+[\w ]+[\s*]+\w+(?: \[\d+\])?;$", l) or re.match(r"^\s+\w+ \*?\w+;$", l): decl.append(l.strip()); continue
                in_decl = False
            body.append(l)
        # drop the function's closing brace
        while body and body[-1].strip() in ("", "}"): body.pop()

        # ---- parameters
        m = re.search(r"\((.*)\)\s*$", sig)
        cparams = []
        if m and m.group(1).strip() and m.group(1).strip() != "void":
            for p in re.split(r",", m.group(1)):
                p = p.strip()
                mm = re.match(r"(.+?)\s*\**\s*(\w+)$", p)
                if mm: cparams.append(mm.group(2))
        expected = ([("this", self.cls)] if not self.m["static"] else []) + [(n, strip_mod(t)) for t, n in self.m["params"]] + [("method", "MethodInfo")]
        rename = {}
        if all(p.startswith("param_") for p in cparams):
            for idx, p in enumerate(cparams):
                if idx < len(expected): rename[p] = expected[idx][0]
        # a local that Ghidra called 'this' (after a callee's parameter) must not clash
        if "this" in [d.split()[-1].rstrip(";") for d in decl]:
            rename["this"] = "obj"
        for n, t in expected: self.types[n] = t
        self.ptrsize = {}
        for d in decl:
            mm = re.match(r"(\w+) \*+(\w+);$", d)
            if mm:
                self.ptrsize[mm.group(2)] = {"long": 8, "ulong": 8, "longlong": 8, "ulonglong": 8, "undefined8": 8, "code": 8, "int": 4, "uint": 4, "undefined4": 4, "float": 4,
                                             "short": 2, "ushort": 2, "undefined2": 2, "char": 1, "byte": 1, "undefined1": 1, "undefined": 1, "bool": 1}.get(mm.group(1), 8)
        body_text = "\n".join(body)
        if rename:
            rx = re.compile(r"\b(" + "|".join(map(re.escape, rename)) + r")\b")
            body_text = rx.sub(lambda mm: rename[mm.group(1)], body_text)
            decl = [rx.sub(lambda mm: rename[mm.group(1)], d) for d in decl]
        body_text = self.rewrite_body(body_text)
        # ---- locals block: only the ones still used, with the inferred types
        used = set(re.findall(r"\b[A-Za-z_]\w*\b", body_text))
        out_decl = []
        for d in decl:
            mm = re.match(r"(.+?)\s*(\**)\s*(\w+)(?: \[(\d+)\])?;$", d)
            if not mm: continue
            name = mm.group(3)
            name = self.renames.get(name, name)
            if name not in used or name in ("this",): continue
            ty = self.types.get(name)
            if ty: ty = short_type(ty)
            elif not re.match(r"[a-z]{1,2}Var\d|[a-z]*Stack_|unaff_|extraout_|in_", name): ty = None
            else: continue
            if ty is None:
                ty = mm.group(1).replace("undefined8", "long").replace("undefined4", "int").replace("undefined1", "byte").replace("undefined2", "short").replace("longlong", "long")
                if mm.group(2): ty = "ptr"
                if mm.group(4): ty += "[" + mm.group(4) + "]"
            out_decl.append("%s %s;" % (ty, name))
        head = self.emit_header()
        res = head
        if out_decl:
            res += "    // locals: " + " ".join(out_decl) + "\n"
        res += body_text.rstrip("\n") + "\n"
        return res

    def emit_header(self):
        m = self.m
        mods = ("static " if m["static"] else "") + ("virtual " if m.get("virtual") else "")
        ps = ", ".join("%s %s" % (short_type(strip_mod(t)), n) for t, n in m["params"])
        name = m["name"]
        if name == ".ctor": name = short_type(self.cls).split(".")[-1]
        if name == ".cctor": name = "static " + short_type(self.cls).split(".")[-1]; mods = ""
        ret = "" if m["name"] in (".ctor", ".cctor") else short_type(m["ret"]) + " "
        return "%s%s%s(%s)   // 0x%X\n" % (mods, ret, name, ps, m["addr"])

    # ---- the body rewrite
    def rewrite_body(self, t):
        self.renames = {}
        t = join_wrapped(t)
        t = strip_boilerplate(t)
        t = re.sub(r",(?=\S)", ", ", t)
        for _ in range(3): t = re.sub(r"<([^<>]*)>", lambda m: "<" + m.group(1).replace(", ", ",").replace(",_", ",") + ">", t)
        t = CAST_PTR.sub("", t)
        t = CAST_INT.sub("", t)
        for v, sz in self.ptrsize.items():
            v = self.renames.get(v, v)
            t = re.sub(r"\b%s\[(0x[0-9a-f]+|\d+)\]" % v, lambda m: "*(%s + 0x%x)" % (v, int(m.group(1), 0) * sz), t)
            t = re.sub(r"\b%s\[(\w+)\]" % v, lambda m: "*(%s + %s * %d)" % (v, m.group(1), sz), t)
            t = re.sub(r"(?<![\w.*])%s \+ (\d+)\b" % v, lambda m: "%s + 0x%x" % (v, int(m.group(1)) * sz), t)
            t = re.sub(r"(?<![\w.*])%s \+ -(\d+)\b" % v, lambda m: "%s - 0x%x" % (v, int(m.group(1)) * sz), t)
            t = re.sub(r"\*%s\b(?!\s*\+)" % v, "*(%s + 0x0)" % v, t)

        t = re.sub(r"\(undefined1\s+\[16\]\)", "", t)
        t = re.sub(r"\b(\w+)\._0_8_\b", r"\1", t)
        t = re.sub(r"\b(\w+)\._0_4_\b", r"\1", t)
        t = re.sub(r"\b(\w+)\._8_8_\b", r"\1_x1", t)
        t = re.sub(r"\b(\w+)\._(\d+)_(\d+)_\b", r"\1_part\2", t)
        # metadata / string / type globals
        t = re.sub(r"func_0x02d2a3c4\(\s*&?\s*([\w<>,\[\]`:]+)\s*(?:,[^()]*)?\)", r"\1", t)
        t = re.sub(r"\n[^\n]*\bil2cpp_class_init\([^;]*\);", "", t)
        t = t.replace("/* WARNING: Subroutine does not return */", "")
        for fn, nm in HELPERS.items():
            if nm == "IDENTITY": continue
            t = t.replace(fn, nm)
        t = re.sub(r"\bfunc_0x[0-9a-f]{8}\b", lambda m: FUNC_NAMES.get(m.group(0), "il2cpp_runtime_" + m.group(0)[7:]), t)
        t = re.sub(r"\n[^\n]*\b(?:NULLCHECK|CLASSINIT|BARRIER)\([^;]*\);", "", t)
        t = re.sub(r"\bList\.Add_inlined\((\w[\w.]*), ([^,()]+(?:\([^()]*\))?), [^()]*\)", r"\1.Add(\2)", t)
        t = re.sub(r"\bBox\(\w+, &?(\w+)\)", r"(object)\1", t)
        t = re.sub(r"\bSUB\d\d\(([^(),]+?)(?: & 1)?, 0\)", r"\1", t)
        t = re.sub(r"\n[^\n]*\b\w+ = (?:extraout_|unaff_|in_)\w+;", "", t)
        t = re.sub(r"&?\b_?(str_[A-Za-z0-9_]*)\b", self.string_literal, t)
        t = re.sub(r"\(\*?(pcRam[0-9a-f]+)\)\(", r"\1(", t)
        t = re.sub(r"icall\(&UNK_([0-9a-f]+)\)", self.icall, t)
        t = re.sub(r"&?\b_?((?:[A-Za-z0-9_\[\]`]|<[^<>]*(?:<[^<>]*>[^<>]*)*>)+)_TypeInfo\b", lambda m: "typeof(" + short_type(TYPE_C.get(m.group(1), m.group(1).rstrip("_").replace("_", "."))).replace(", ", ",").replace("<.", "<").replace(",.", ",") + ")", t)
        t = re.sub(r"\*\(undefined\d?\s*\(\*\)\s*\[\d+\]\)\s*&?", "", t)
        t = self.interface_calls(t)
        t = self.icalls(t)
        t = re.sub(r"&?\b_?Method_([A-Za-z0-9_<>,`\[\]]+?)__\b", r"method:\1", t)
        # a variable that only holds a TypeInfo: use the type directly
        for m in re.finditer(r"^\s*(\w+) = (typeof\([^()]*\));$", t, re.M):
            v = m.group(1)
            if len(re.findall(r"\b%s = " % v, t)) == 1:
                t = t.replace(m.group(0), "")
                t = re.sub(r"\b%s\b" % v, m.group(2), t)
        t = re.sub(r"\n\s*(?:typeof\([^()]*\)|\"[^\"\n]*\"|method:[\w<>,`\[\]]+);", "", t)
        t = re.sub(r"method:((?:[A-Za-z0-9_\[\]`]|<[^<>]*(?:<[^<>]*>[^<>]*)*>)+)", self.method_ref, t)
        # il2cpp_object_new(typeof(X)) -> new X
        t = re.sub(r"il2cpp_object_new\(typeof\(([^()]*)\)(?:, \w+)?\)", r"new \1", t)
        t = re.sub(r"il2cpp_array_new\(typeof\(([^()]*)\),\s*([^()]*)\)", r"new \1[\2]", t)
        t = re.sub(r"il2cpp_throw_null_reference\(\);", "throw new NullReferenceException();", t)
        t = re.sub(r"(\w+)__ThrowNullReferenceException\([^;]*\);", "throw new NullReferenceException();", t)
        # field access, calls, typing: iterate to a fixed point
        for rnd in range(2):
            for _ in range(10):
                before = t
                t = self.rewrite_fields(t)
                self.infer_types(t)
                t = self.rewrite_calls(t)
                self.infer_types(t)
                if t == before: break
            if rnd == 0: t = self.split_versions(t)
        t = re.sub(r"&((?:this|\w+)(?:\.[\w<>]+)*) = ", r"\1 = ", t)
        t = re.sub(r"\n\s*\w+ = [\w.<>]+\.__statics;", "", t)
        t = re.sub(r"(\w+) = &([\w.<>]+);\n\s*\*\(\1 \+ 0x0\) = (.+);", r"\2 = \3;", t)
        t = self.variable_offsets(t)
        t = self.virtual_names(t)
        t = self.bogus_receivers(t)
        t = self.rewrite_enums(t)
        t = self.rename_locals(t)
        t = self.nulls(t)
        t = tidy(t)
        return t

    def variable_offsets(self, t):
        """*(obj + v) where v was set to one of several constants: name the fields on the constants"""
        for m in re.finditer(r"\*\(((?:this|\w+)(?:\.[\w<>]+)*) \+ (\w+)\)", t):
            base, v = m.group(1), m.group(2)
            bt = self.type_of(base)
            if not bt or re.fullmatch(r"0x[0-9a-f]+|\d+", v): continue
            def ann(mm):
                r = field_at(bt, int(mm.group(1), 0))
                return mm.group(0) + ("   // %s.%s" % (base, r[0]) if r else "")
            t = re.sub(r"\b%s = (0x[0-9a-f]+|\d+);" % re.escape(v), ann, t)
            t = t.replace(m.group(0), "%s.[field at %s]" % (base, v))
        return t

    def virtual_names(self, t):
        def rep(m):
            path, slot = m.group(1), int(m.group(2))
            ty = self.type_of(path)
            k = resolve_type(ty)[0] if ty else None
            if k and MODEL[k]["kind"] == "interface": k = "System.Object"
            seen = set()
            while k and k not in seen:
                seen.add(k)
                for mm in MODEL[k]["methods"]:
                    if mm.get("slot") == slot: self.xref.add(("c", method_id(k, mm)))
                    if mm.get("slot") == slot: return "%s.%s(" % (path, mm["name"].replace("get_", "").replace("set_", "") if mm["name"][:4] in ("get_", "set_") else mm["name"])
                base = MODEL[k]["base"].split(",")[0].strip() if MODEL[k]["base"] else ""
                k = resolve_type(base, MODEL[k]["ns"])[0] if base else None
            return m.group(0)
        return re.sub(r"((?:this|\w+)(?:\.[\w<>]+)*)\.virtual_slot_(\d+)\(", rep, t)

    def bogus_receivers(self, t):
        """x.member where x holds a bool/int: the decompiler lost the receiver; it is this when the member is ours"""
        params = {n for n, _ in self.fixed_params()}
        mine = set()
        for off, (n, _) in fields_of(self.cls).items(): mine.add(n)
        k = resolve_type(self.cls)[0]
        if k:
            for p in MODEL[k]["props"]: mine.add(p["name"])
            for m in MODEL[k]["methods"]: mine.add(m["name"])
        def rep(m):
            v, member = m.group(1), m.group(2)
            ty = self.types.get(v)
            if v not in params and ty in ("bool", "int", "uint", "float", "byte") and member in mine: return "this." + member
            return m.group(0)
        return re.sub(r"\b([a-z]{1,2}Var\d+(?:_\d+)?)\.(\w+)", rep, t)

    def nulls(self, t):
        def rep(m):
            path, op = m.group(1), m.group(2)
            ty = self.type_of(path)
            if ty and ty not in PRIM - {"string", "object"} and not is_enum(ty) and not is_value_type(ty):
                return "%s %s null" % (path, op)
            return m.group(0)
        return re.sub(r"((?:this|\w+)(?:\.[\w<>]+)*) (==|!=) (?:0|0x0)\b", rep, t)

    IFACE = re.compile(r"(\w+) = \*\(?(\w+)(?: \+ 0x0\))?;(?:\s*\w+ = typeof\([^()]*\);)?\s*(\w+) = \*\(\1 \+ 0x12e\);\s*if\(\3 != 0\) \{[^{}]*?\{[^{}]*?typeof\(([\w.<>]+)\)\) \{\s*(\w+) = \(\1 \+ (?:\(\*\(?\w+(?: \+ 0x0\))? \+ (\d+)\)|\*\(?\w+(?: \+ 0x0\))?) \* 0x10 \+ 0x138\);\s*goto (LAB_\w+);[^{}]*?\}[^{}]*?\}[^{}]*?\}\s*\5 = IsInst\(\2, typeof\(\4\), \d+\);\s*\7:\s*((?:\w+ = )?)\(\*\*\(?\5(?: \+ 0x0\))?\)\(([^;]*)\);", re.S)
    VCALL = re.compile(r"\(\*\*\(\*\(?(\w+)(?: \+ 0x0\))? \+ (0x[0-9a-f]+)\)\)\(\1, ?(.*?),? ?\*\(\*\(?\1(?: \+ 0x0\))? \+ 0x[0-9a-f]+\)\)")

    def interface_calls(self, t):
        """IL2CPP's inline interface dispatch: a loop over the class's interface offsets, with IsInst(obj, typeof(I), slot)
        as the fallback, then a call through the found vtable entry. Rewritten as obj.Method(args)."""
        lines = t.split("\n")
        out = []
        i = 0
        FALLBACK = re.compile(r"^(\s*)(\w+) =\s+IsInst\((\w+), typeof\(([^()]+)\), (\d+)\);$")
        while i < len(lines):
            m = FALLBACK.match(lines[i])
            if m and i + 2 < len(lines) and re.match(r"^\s*LAB_\w+:$", lines[i + 1]):
                ind, P, obj, iface, slot = m.group(1), m.group(2), m.group(3), m.group(4), int(m.group(5))
                cm = re.match(r"^\s*((?:\w+ = )?)\(\*\*\(?" + re.escape(P) + r"(?: \+ 0x0\))?\)\((.*)\);$", lines[i + 2])
                if not cm:
                    # the call is elsewhere (shared by several paths): keep a readable lookup, drop the loop
                    j = len(out) - 1; cut = None
                    for back in range(1, 16):
                        if j - back < 0: break
                        if re.match(r"^\s*\w+ = \*\(?\w+(?: \+ 0x0\))?;$", out[j - back]) and "0x12e" in (out[j - back + 1] if j - back + 1 < len(out) else ""):
                            cut = j - back; break
                    if cut is not None and all(("{" in x or "}" in x or "0x12e" in x or "0xb0" in x or "typeof(" in x or "goto" in x or "0x138" in x or "- 1;" in x or "+ 0x10;" in x or x.strip().startswith("do") or x.strip().startswith("} while") or x.strip() == "" or "TrafficStats" in x) for x in out[cut + 1:]):
                        del out[cut:]
                    out.append("%s%s = InterfaceMethod(%s, typeof(%s), %d);" % (ind, P, obj, short_type(iface), slot))
                    i += 1
                    continue
                if cm:
                    args = [x.strip() for x in split_args(cm.group(2))]
                    a = args[1:-1] if len(args) >= 2 else args
                    k = resolve_type(iface)[0]
                    name = "slot%d" % slot
                    rtype = "object"
                    if k:
                        for mm in MODEL[k]["methods"]:
                            if mm.get("slot") == slot: name = mm["name"]; rtype = mm["ret"]; self.xref.add(("c", method_id(k, mm))); break
                    call = render_call(obj, name, a, rtype)
                    self.calltypes = getattr(self, "calltypes", {})
                    self.calltypes[call] = rtype
                    # drop the dispatch loop before it: back to the class-pointer load
                    j = len(out) - 1; cut = None
                    for back in range(1, 16):
                        if j - back < 0: break
                        l = out[j - back]
                        if re.match(r"^\s*\w+ = \*\(?" + re.escape(obj) + r"(?: \+ 0x0\))?;$", l) or re.match(r"^\s*\w+ = \*\(?\w+(?: \+ 0x0\))?;$", l) and "0x12e" in (out[j - back + 1] if j - back + 1 < len(out) else ""):
                            cut = j - back; break
                    if cut is not None and all(("{" in x or "}" in x or "0x12e" in x or "0xb0" in x or "typeof(" in x or "goto" in x or "0x138" in x or "- 1;" in x or "+ 0x10;" in x or x.strip().startswith("do") or x.strip().startswith("} while") or x.strip() == "" or "= typeof(" in x) for x in out[cut + 1:]):
                        del out[cut:]
                    out.append("%s%s%s;   // interface call %s" % (ind, cm.group(1), call, short_type(iface)))
                    i += 3
                    continue
            out.append(lines[i]); i += 1
        t = "\n".join(out)
        def vc(m):
            obj, off, args = m.group(1), int(m.group(2), 16), m.group(3)
            return "%s.virtual_slot_%d(%s)" % (obj, (off - 0x138) // 0x10, args)
        return self.VCALL.sub(vc, t)

    def icalls(self, t):
        names = {}
        def rep(m):
            full = m.group(2)
            mm = re.match(r"([\w.]+)::(\w+)", full)
            names[m.group(1)] = (short_type(mm.group(1)) + "." + mm.group(2)) if mm else full
            return ""
        t = re.sub(r"\s*(pcRam[0-9a-f]+) = icall\(\"([^\"]*)\"\);", rep, t)
        for v, n in names.items():
            t = re.sub(r"\b" + v + r"\(", n + "(", t)
        return t

    def method_ref(self, m):
        """method:Class_Method -> Class.Method (a delegate target / generic method-info)"""
        name = m.group(1)
        for i in [x.start() for x in re.finditer("_", name)][::-1]:
            c = CALLEES.get((name[:i], name[i + 1:]))
            if c:
                self.xref.add(("c", method_id(c[0][0], c[0][1])))
                return short_type(c[0][0]) + "." + name[i + 1:]
        return "method:" + name

    def string_literal(self, m):
        vals = STRINGS.get(m.group(1))
        if not vals: return m.group(0).lstrip("&_")
        if len(vals) == 1: return c_string(next(iter(vals)))
        if m.group(1) == "str_": return '""'
        return c_string(max(sorted(vals), key=len)) + "/*~*/"

    def icall(self, m):
        s = so_string(int(m.group(1), 16))
        return 'icall("%s")' % s if s else m.group(0)

    FIELD = re.compile(r"\*\(((?:this|\w+)(?:\.[\w<>]+)*) \+ (0x[0-9a-f]+|\d+)\)")
    FIELD_ADDR = re.compile(r"(?<![\w.*])\(((?:this|\w+)(?:\.[\w<>]+)*) \+ (0x[0-9a-f]+|\d+)\)|(?<![\w.*(])((?:this|\w+)(?:\.[\w<>]+)*) \+ (0x[0-9a-f]+|\d+)(?=[,)])")
    ARRAY = re.compile(r"\*\(((?:this|\w+)(?:\.[\w<>]+)*) \+ 0x20 \+ (?:\(int\))?(\w+) \* (?:8|4|2|1)\)")
    ARRAY2 = re.compile(r"\*\(((?:this|\w+)(?:\.[\w<>]+)*) \+ (?:\(int\))?(\w+) \* (?:8|4|2|1) \+ 0x20\)")
    ARRAY3 = re.compile(r"\*\(((?:this|\w+)(?:\.[\w<>]+)*) \+ (\w+) \* (?:8|4|2|1)\)")
    STATIC = re.compile(r"\*\(\*\(typeof\(([^()]*)\) \+ 0xb8\) \+ (0x[0-9a-f]+|\d+)\)|\*\*\(typeof\(([^()]*)\) \+ 0xb8\)()")

    def note_field(self, bt, off, name, how):
        """Cross-reference: a read or write of the field `name` (maybe "field.sub" inside a struct) of a bt object."""
        fs = fields_of(bt)
        top = off if off in fs else max((o for o in fs if o < off), default=None)
        if top is None: return
        owner = field_owner(bt, top)
        if owner: self.xref.add((how, owner + "." + name.split(".")[0]))

    def rewrite_fields(self, t):
        def statics(m):
            self.types[m.group(1) + ".__statics"] = "static:" + m.group(1)
            return m.group(1) + ".__statics"
        t = re.sub(r"\*\(typeof\(([^()]*)\) \+ 0xb8\)", statics, t)
        def arr(m):
            base, idx = m.group(1), m.group(2)
            bt = self.type_of(base)
            if bt and bt.endswith("[]"): self.types[base + "[" + idx + "]"] = bt[:-2]
            return "%s[%s]" % (base, idx)
        t = self.ARRAY.sub(arr, t)
        t = self.ARRAY2.sub(arr, t)
        def st(m):
            tyname, off = (m.group(1), int(m.group(2), 0)) if m.group(1) else (m.group(3), 0)
            full = resolve_type(tyname)[0]
            sf = static_fields_of(tyname)
            if off in sf:
                self.types[tyname + "." + sf[off][0]] = sf[off][1]
                if full: self.xref.add(("w" if is_store(m) else "r", full + "." + sf[off][0]))
                return tyname + "." + sf[off][0]
            return m.group(0)
        t = self.STATIC.sub(st, t)
        def fld(m):
            base, off = m.group(1), int(m.group(2), 0)
            bt = self.type_of(base)
            if not bt: return m.group(0)
            if bt.startswith("static:"):
                sf = static_fields_of(bt[7:])
                if off in sf:
                    self.types[bt[7:] + "." + sf[off][0]] = sf[off][1]
                    k = resolve_type(bt[7:])[0]
                    if k: self.xref.add(("w" if is_store(m) else "r", k + "." + sf[off][0]))
                    return bt[7:] + "." + sf[off][0]
                top = max((o for o in sf if o < off), default=None)      # inside a struct-typed static (ObscuredBool...)
                if top is not None:
                    r = field_at(sf[top][1], off - top)
                    if r:
                        self.types[bt[7:] + "." + sf[top][0] + "." + r[0]] = r[1]
                        k = resolve_type(bt[7:])[0]
                        if k: self.xref.add(("w" if is_store(m) else "r", k + "." + sf[top][0]))
                        return bt[7:] + "." + sf[top][0] + "." + r[0]
                return m.group(0)
            if bt.endswith("[]"):
                if off == 0x18: return base + ".Length"
                if off >= 0x20:
                    es = elem_size(bt[:-2])
                    if (off - 0x20) % es == 0:
                        self.types[base + "[" + str((off - 0x20) // es) + "]"] = bt[:-2]
                        return "%s[%d]" % (base, (off - 0x20) // es)
                return m.group(0)
            r = field_at(bt, off)
            if not r: return m.group(0)
            self.types[base + "." + r[0]] = r[1]
            self.note_field(bt, off, r[0], "w" if is_store(m) else "r")
            return base + "." + r[0]
        t = self.FIELD.sub(fld, t)
        def st0(m):          # *X.__statics : the static field at offset 0 (usually the singleton Instance)
            tyname = m.group(1)
            sf = static_fields_of(tyname)
            if 0 in sf:
                self.types[tyname + "." + sf[0][0]] = sf[0][1]
                k = resolve_type(tyname)[0]
                if k: self.xref.add(("w" if is_store(m) else "r", k + "." + sf[0][0]))
                return tyname + "." + sf[0][0]
            return m.group(0)
        t = re.sub(r"\*(\w[\w.<>,]*?)\.__statics\b", st0, t)
        def deref(m):
            v = m.group(1)
            ty = self.type_of(v)
            if ty and is_value_type(ty):
                r = field_at(ty, 0)
                if r:
                    self.types[v + "." + r[0]] = r[1]
                    return v + "." + r[0]
            return m.group(0)
        t = re.sub(r"(?<![\w.)])\*(\w+)\b(?!\s*[+\-*(\[])", deref, t)
        # &(obj + 0x30) style address-of a field passed to a call, e.g. a struct field
        def fa(m):
            base, off = (m.group(1), int(m.group(2), 0)) if m.group(1) else (m.group(3), int(m.group(4), 0))
            bt = self.type_of(base)
            if not bt or bt.endswith("[]"): return m.group(0)
            if bt.startswith("static:"):
                sf = static_fields_of(bt[7:])
                return "&" + bt[7:] + "." + sf[off][0] if off in sf else m.group(0)
            r = field_at(bt, off)
            if not r: return m.group(0)
            self.note_field(bt, off, r[0], "r")
            return "&" + base + "." + r[0]
        t = self.FIELD_ADDR.sub(fa, t)
        return t

    CALL = re.compile(r"(?<![\w.])([A-Za-z_][A-Za-z0-9_<>,`\[\]]*__[A-Za-z0-9_<>,`\[\]]*)\(")

    def rewrite_calls(self, t):
        self.weak = getattr(self, "weak", set())
        self._assign_pos = {}
        for pos, v, _ in self.assignments(t): self._assign_pos.setdefault(v, pos)
        out = []
        pos = 0
        for m in self.CALL.finditer(t):
            if m.start() < pos: continue
            cname = m.group(1)
            if cname.startswith("il2cpp_"): continue
            cands = find_callee(cname)
            # find the matching ')'
            depth, j = 1, m.end()
            while j < len(t) and depth:
                if t[j] == "(": depth += 1
                elif t[j] == ")": depth -= 1
                j += 1
            if depth: break
            argtext = t[m.end():j - 1]
            args = split_args(argtext)
            if not cands:
                continue
            if args and re.fullmatch(r"\s*(0|0x0|method(?:_\d+)?|method:\S+|_?Method_\S+)\s*", args[-1]): args = args[:-1]
            full, meth = pick_overload(cands, args)
            self.xref.add(("c", method_id(full, meth)))
            cls_short = short_type(full)
            out_var = None
            if is_value_type(meth["ret"]) and args and args[0].strip().startswith("&") and len(args) > len(meth["params"]) + (0 if meth["static"] else 1):
                out_var = args[0].strip()[1:]; args = args[1:]
                self.types[out_var] = meth["ret"]
            # drop the MethodInfo argument
            nargs = len(meth["params"]) + (0 if meth["static"] else 1)
            if len(args) > nargs: args = args[:nargs]
            # instance call
            name = meth["name"]
            if meth["static"]:
                recv, rest = cls_short, args
            else:
                if not args: continue
                recv, rest = args[0].strip(), args[1:]
                if recv.startswith("&"): recv = recv[1:]
                rt = self.type_of(recv)
                if re.fullmatch(r"\w+", recv) and recv != "this":
                    assigned_before = recv in self._assign_pos and self._assign_pos[recv] < t.rfind("\n", 0, m.start())
                    if (rt in PRIM and rt not in ("object",)) or (rt is None and not assigned_before and recv not in {n for n, _ in self.fixed_params()}):
                        if same_class(self.cls, full): recv = "this"
                    if rt is None and recv != "this" and not gparams_of(full):
                        self.types[recv] = full; self.weak.add(recv)
            # generic instantiation from the method-info argument: List<object>.get_Item with method:List<CardScript>_get_Item
            inst_full = full
            mi = re.search(r"method:([A-Za-z0-9_<>,`\[\]]+?)_" + re.escape(name) + r"\b", argtext)
            rtype = meth["ret"]
            mg = generic_base(meth["name"])[1]      # generic method: SelectMany<TSource,TResult>
            if mg:
                mi2 = re.search(r"method:[\w.]*?" + re.escape(meth["name"].split("<")[0]) + r"<([^<>]*(?:<[^<>]*>[^<>]*)*)>", argtext)
                if mi2:
                    margs = generic_base("x<" + mi2.group(1).replace("_", ".") + ">")[1]
                    if len(margs) == len(mg):
                        subst = dict(zip(mg, margs))
                        rtype = re.sub(r"\b(" + "|".join(map(re.escape, subst)) + r")\b", lambda mm: subst[mm.group(1)], rtype)
            name = meth["name"].split("<")[0]
            gbase, gparams = generic_base(full)
            if gparams:
                subst = None
                rt = self.type_of(recv) if not meth["static"] else None
                if rt:
                    _, rargs = generic_base(strip_mod(rt))
                    if len(rargs) == len(gparams): subst = dict(zip(gparams, rargs))
                if subst is None and mi:
                    _, margs = generic_base(mi.group(1).replace("_", "."))
                    if len(margs) == len(gparams): subst = dict(zip(gparams, margs))
                if subst:
                    rtype = re.sub(r"\b(" + "|".join(map(re.escape, subst)) + r")\b", lambda mm: subst[mm.group(1)], rtype)
            # typed parameters: enum arguments by name
            prs = meth["params"]
            rest2 = []
            for k, a in enumerate(rest):
                a = a.strip()
                if k < len(prs) and re.fullmatch(r"[a-z]{1,3}Var\d+(?:_\d+)?|\w+Stack_\w+", a) and a not in self.types:
                    pt = strip_mod(prs[k][0])
                    if pt not in PRIM and not pt.endswith("[]"): self.types[a] = pt; self.weak.add(a)
                if k < len(prs) and is_enum(prs[k][0]) and re.fullmatch(r"-?(0x[0-9a-f]+|\d+)", a):
                    en = enum_name(prs[k][0], int(a, 0))
                    if en: a = short_type(prs[k][0]).split(".")[-1] + "." + en
                rest2.append(a)
            rest = rest2
            # render
            expr = render_call(recv, name, rest, rtype)
            if False:
                expr = "%s.%s" % (recv, name[4:])
            elif name.startswith("set_") and len(rest) == 1 and name != "set_Item":
                expr = "%s.%s = %s" % (recv, name[4:], rest[0])
            elif name == "get_Item" and len(rest) == 1:
                expr = "%s[%s]" % (recv, rest[0])
            elif name == "set_Item" and len(rest) == 2:
                expr = "%s[%s] = %s" % (recv, rest[0], rest[1])
            elif name == "op_Equality" and len(rest) == 2:
                expr = "%s == %s" % tuple(rest)
            elif name == "op_Inequality" and len(rest) == 2:
                expr = "%s != %s" % tuple(rest)
            elif name == "op_Implicit" and len(rest) == 1:
                expr = "(%s)%s" % (short_type(rtype), rest[0])
            if out_var: expr = "%s = %s" % (out_var, expr)
            self.calltypes = getattr(self, "calltypes", {})
            self.calltypes[expr] = rtype
            out.append(t[pos:m.start()]); out.append(expr)
            pos = j
        out.append(t[pos:])
        return "".join(out)

    ASSIGN = re.compile(r"^\s*((?:this|\w+)(?:\.[\w<>]+)*) = (.+);$", re.M)

    def assignments(self, t):
        """every 'v = expr' in the text, statement-level and inside conditions -> [(start, var, expr)]"""
        res = []
        for m in re.finditer(r"(?<![=!<>+\-*/&|^])\b((?:this|\w+)(?:\.[\w<>]+)*) = (?!=)", t):
            j = m.end(); depth = 0
            while j < len(t):
                ch = t[j]
                if ch in "([": depth += 1
                elif ch in ")]":
                    if depth == 0: break
                    depth -= 1
                elif ch in ";\n" or (ch == "," and depth == 0): break
                j += 1
            res.append((m.start(), m.group(1), t[m.end():j].strip()))
        return res

    def expr_type(self, expr):
        ct = getattr(self, "calltypes", {})
        if expr in ct: return ct[expr]
        if re.fullmatch(r"(?:this|\w+)(?:\.[\w<>]+)+", expr): return self.type_of(expr)
        if expr.endswith(".__statics"): return "static:" + expr[:-10]
        if re.fullmatch(r"(?:this|\w+)(?:\.[\w<>]+)*\[\w+\]", expr): return self.types.get(expr)
        if expr.startswith("new ") and "[" not in expr: return expr[4:].strip()
        if expr.startswith("new ") and "[" in expr: return expr[4:expr.index("[")].strip() + "[]"
        if expr.startswith("IsInst("):
            a = split_args(expr[7:-1])
            if len(a) >= 2 and a[1].strip().startswith("typeof("): return a[1].strip()[7:-1]
        if re.fullmatch(r"\w+", expr): return self.types.get(expr)
        if re.fullmatch(r"\*\(?\w+(?: \+ 0x0\))?", expr): return "Il2CppClass"
        if re.search(r" (==|!=|<|>|<=|>=|&&|\|\|) ", expr) and "(" not in expr.split(" ")[0]: return "bool"
        m = re.fullmatch(r"\((\w[\w.<>\[\]]*)\)(\w+)", expr)   # (CardScript)x
        if m and resolve_type(m.group(1))[0]: return m.group(1)
        return None

    def infer_types(self, t):
        self.weak = getattr(self, "weak", set())
        params = {n for n, _ in self.fixed_params()}
        ret = strip_mod(self.m["ret"])
        if ret not in ("void",):
            for v in re.findall(r"\breturn (\w+);", t):
                if v not in params and v not in self.types and v not in ("true", "false", "null"): self.types[v] = ret; self.weak.add(v)
        for m in re.finditer(r"^\s*((?:this|\w+)(?:\.[\w<>]+)+) = (\w+);$", t, re.M):
            ft = self.type_of(m.group(1)); v = m.group(2)
            if ft and v not in params and v not in self.types and not re.fullmatch(r"\d+|0x[0-9a-f]+|true|false|null", v): self.types[v] = ft; self.weak.add(v)
        for m in re.finditer(r"\b(\w+) (?:==|!=) ((?:this|\w+)(?:\.[\w<>]+)+)\b|\b((?:this|\w+)(?:\.[\w<>]+)+) (?:==|!=) (\w+)\b", t):
            v, path = (m.group(1), m.group(2)) if m.group(1) else (m.group(4), m.group(3))
            ft = self.type_of(path)
            if ft and v not in params and v not in self.types and not re.fullmatch(r"\d+|0x[0-9a-f]+|true|false|null", v): self.types[v] = ft; self.weak.add(v)
        for _, var, expr in self.assignments(t):
            if "." in var or var in params: continue
            ty = self.expr_type(expr)
            if ty and ty != "void" and (var not in self.types or var in getattr(self, "weak", set())):
                self.types[var] = ty
                if var in getattr(self, "weak", set()): self.weak.discard(var)

    def split_versions(self, t):
        """Ghidra reuses one variable for values of different types; give each a name of its own in reading order."""
        params = {n for n, _ in self.fixed_params()}
        cur = {}       # original var -> current name
        counter = {}
        edits = []     # (start, end, replacement) over the text, to apply at the end
        assigns = self.assignments(t)
        assign_at = {a[0]: a for a in assigns}
        assign_end = {a[0]: a[0] + len(a[1]) + 3 + len(a[2]) for a in assigns}
        pending = []
        # walk tokens in order
        pos_assign = sorted(assign_at)
        tokens = list(re.finditer(r"\b(?:this|[A-Za-z_]\w*)\b", t))
        ai = 0
        for tok in tokens:
            v = tok.group(0)
            for pe in [x for x in pending if x[0] <= tok.start()]:
                cur[pe[1]] = pe[2]; pending.remove(pe)
            if v in params or v == "this" or "." in v: continue
            # is this token the LHS of an assignment?
            a = assign_at.get(tok.start())
            if a is not None:
                _, var, expr = a
                if var == v:
                    ty = self.expr_type(expr)
                    base_ty = self.types.get(v)
                    name = cur.get(v, v)
                    cur_ty = self.types.get(name)
                    if ty and cur_ty and ty != cur_ty and short_type(ty) != short_type(cur_ty):
                        counter[v] = counter.get(v, 1) + 1
                        name = "%s_%d" % (v, counter[v])
                        while name in self.types: counter[v] += 1; name = "%s_%d" % (v, counter[v])
                        self.types[name] = ty
                    elif ty and not cur_ty:
                        self.types[name] = ty
                    pending.append((assign_end[tok.start()], v, name))
                    if name != v: edits.append((tok.start(), tok.end(), name))
                    continue
            name = cur.get(v)
            if name and name != v: edits.append((tok.start(), tok.end(), name))
        if not edits: return t
        out, p = [], 0
        for s, e, r in edits:
            out.append(t[p:s]); out.append(r); p = e
        out.append(t[p:])
        return "".join(out)

    def fixed_params(self):
        return [(n, None) for n in (["this"] if not self.m["static"] else []) + [n for _, n in self.m["params"]]]

    CMP = re.compile(r"((?:this|\w+)(?:\.[\w<>]+)*) (==|!=|=|<|>|<=|>=) (-?(?:0x[0-9a-f]+|\d+))\b")
    CMP2 = re.compile(r"(-?(?:0x[0-9a-f]+|\d+)) (==|!=|<|>|<=|>=) ((?:this|\w+)(?:\.[\w<>]+)*)\b")

    def rewrite_enums(self, t):
        def en(m):
            path, op, val = m.group(1), m.group(2), m.group(3)
            ty = self.type_of(path)
            if ty and is_enum(ty):
                n = enum_name(ty, int(val, 0))
                if n: return "%s %s %s.%s" % (path, op, short_type(ty).split(".")[-1], n)
            return m.group(0)
        t = self.CMP.sub(en, t)
        def en2(m):
            val, op, path = m.group(1), m.group(2), m.group(3)
            ty = self.type_of(path)
            if ty and is_enum(ty):
                n = enum_name(ty, int(val, 0))
                if n: return "%s.%s %s %s" % (short_type(ty).split(".")[-1], n, op, path)
            return m.group(0)
        return self.CMP2.sub(en2, t)

    def rename_locals(self, t):
        params = {n for n, _ in self.fixed_params()} | {"method"}
        taken = set(params) | {"this"}
        cands = {}
        for m in self.ASSIGN.finditer(t):
            var, expr = m.group(1), m.group(2).strip()
            if "." in var or var in params or not re.fullmatch(r"(?:[a-z]+Var|[a-z]*Stack_|local_|in_|unaff_)\w*|[a-z]+Var\d+|[a-z]{1,2}Var\d+", var): continue
            if var in cands: continue
            name = None
            mm = re.fullmatch(r"(?:this|\w+)(?:\.[\w<>]+)*\.([\w<>]+)", expr)
            if mm: name = mm.group(1)
            else:
                mm = re.fullmatch(r"(?:[\w.<>]+)\.(\w+)\((.*)\)", expr)
                if mm:
                    n = mm.group(1)
                    n = re.sub(r"^(get_|Get|Create|Find|Load|Build|Make|Calculate|Compute|To|Read|Fetch|Try)(?=[A-Za-z])", "", n)
                    name = n if n and n != "ctor" and not n.startswith("virtual_slot") and not n.startswith("slot") else None
                elif expr.startswith("new "): name = re.sub(r"[^A-Za-z0-9]", "", expr[4:].split("[")[0].split("<")[0].split(".")[-1])
                elif re.fullmatch(r"(?:this|\w+)(?:\.[\w<>]+)*\[\w+\]", expr):
                    name = re.sub(r"s$", "", expr.rsplit(".", 1)[-1].split("[")[0]) or None
                    if name and re.fullmatch(r"[a-z]{1,2}Var\d+(?:_\d+)?|this|obj", name): name = None
            if not name: continue
            name = re.sub(r"[^A-Za-z0-9_]", "", name)
            if name == "ctor": continue
            if not name or name[0].isdigit(): continue
            name = lower_first(name)
            if name in ("this", "value", "method") or name in taken:
                k = 2
                while name + str(k) in taken: k += 1
                name = name + str(k)
            taken.add(name); cands[var] = name
        # locals Ghidra named this_NN, and locals known only by the class whose method they are passed to
        for v in sorted(set(re.findall(r"\b(?:this_\d+|[a-z]{1,2}Var\d+(?:_\d+)?)\b", t))):
            if v in cands or v in params: continue
            ty = self.types.get(v)
            if ty and len(ty) > 2 and resolve_type(ty)[0] and not is_enum(ty) and not is_value_type(ty):
                name = lower_first(short_type(ty).split(".")[-1].split("<")[0])
                name = re.sub(r"[^A-Za-z0-9_]", "", name)
                if not name or name in ("this",): continue
                if name in taken:
                    k = 2
                    while name + str(k) in taken: k += 1
                    name = name + str(k)
                taken.add(name); cands[v] = name
        # loop counters
        for m in re.finditer(r"\b([a-z]+Var\d+) = \1 \+ 1;", t):
            v = m.group(1)
            if v not in cands:
                for n in ("i", "j", "k", "n"):
                    if n not in taken: cands[v] = n; taken.add(n); break
        self.renames = cands
        for k, v in cands.items():
            t = re.sub(r"\b" + re.escape(k) + r"\b", v, t)
            if k in self.types: self.types[v] = self.types[k]
        return t

def elem_size(t):
    t = strip_mod(t)
    if t in ("bool", "byte", "sbyte"): return 1
    if t in ("short", "ushort", "char"): return 2
    if t in ("int", "uint", "float") or is_enum(t): return 4
    if is_value_type(t): return 8   # unknown; most small structs
    return 8

def method_id(full, meth):
    """How the cross-reference index names a method: its code address, or Type::Name when it has no code
    (interface and abstract methods, generic definitions, the engine's own)."""
    return "%x" % meth["addr"] if meth.get("addr", -1) > 0 else "%s::%s" % (full, meth["name"])

def field_owner(t, off):
    """The model key of the type that declares the instance field at this offset of t (base classes walked)."""
    k, _ = resolve_type(t)
    seen = set()
    while k and k not in seen:
        seen.add(k)
        if k in KNOWN_LAYOUT:
            if off in KNOWN_LAYOUT[k]: return k
        else:
            for f in MODEL[k]["fields"]:
                if not f["static"] and not f["const"] and f["offset"] == off: return k
        base = MODEL[k]["base"].split(",")[0].strip() if MODEL[k]["base"] else ""
        if not base or base in ("ValueType", "Enum"): return None
        if base.startswith("MulticastDelegate") or base == "Delegate": k = "System.Delegate"; continue
        k = resolve_type(base, MODEL[k]["ns"])[0]
    return None

def is_store(m):
    """True when the matched expression is the left side of a plain assignment."""
    after = m.string[m.end():m.end() + 4]
    return after[:3] == " = " and after[3:4] != "="

def gparams_of(full):
    return generic_base(full)[1]

def same_class(a, b):
    """b is a or a base of a"""
    k = resolve_type(a)[0]; kb = resolve_type(b)[0]
    seen = set()
    while k and k not in seen:
        if k == kb: return True
        seen.add(k)
        base = MODEL[k]["base"].split(",")[0].strip() if MODEL[k]["base"] else ""
        if not base: break
        k = resolve_type(base)[0]
    return False

def render_call(recv, name, rest, rtype="object"):
    if name.startswith("get_") and not rest and name != "get_Item": return "%s.%s" % (recv, name[4:])
    if name.startswith("set_") and len(rest) == 1 and name != "set_Item": return "%s.%s = %s" % (recv, name[4:], rest[0])
    if name == "get_Item" and len(rest) == 1: return "%s[%s]" % (recv, rest[0])
    if name == "set_Item" and len(rest) == 2: return "%s[%s] = %s" % (recv, rest[0], rest[1])
    if name == "op_Equality" and len(rest) == 2: return "%s == %s" % tuple(rest)
    if name == "op_Inequality" and len(rest) == 2: return "%s != %s" % tuple(rest)
    if name == "op_Implicit" and len(rest) == 1: return "(%s)%s" % (short_type(rtype), rest[0])
    if name == ".ctor": return "%s.ctor(%s)" % (recv, ", ".join(rest))
    return "%s.%s(%s)" % (recv, name, ", ".join(rest))

def split_args(s):
    out, depth, cur = [], 0, ""
    for ch in s:
        if ch in "([<": depth += 1
        elif ch in ")]>": depth -= 1
        if ch == "," and depth == 0: out.append(cur); cur = ""
        else: cur += ch
    if cur.strip(): out.append(cur)
    return out

def pick_overload(cands, args):
    n = len(args)
    for full, m in cands:
        if len(m["params"]) + (0 if m["static"] else 1) == n: return full, m
    for full, m in cands:
        if len(m["params"]) + (0 if m["static"] else 1) + 1 == n: return full, m
    return cands[0]

def short_type(t):
    t = strip_mod(t)
    t = re.sub(r"\b(System\.Collections\.Generic|System\.Collections|UnityEngine\.UI|UnityEngine|System|CodeStage\.AntiCheat\.ObscuredTypes|Everguild\.LiveOps|Everguild)\.", "", t)
    return t

# ---------------------------------------------------------------- boilerplate removal
def strip_boilerplate(t):
    lines = t.split("\n")
    out = []
    i = 0
    def block_end(start):
        depth = 0
        for j in range(start, len(lines)):
            depth += lines[j].count("{") - lines[j].count("}")
            if depth <= 0: return j
        return len(lines) - 1
    while i < len(lines):
        l = lines[i]
        s = l.strip()
        if re.match(r"if ?\(\((?:b|c)Ram[0-9a-f]+ & 1\) == 0\) \{", s) or re.match(r"if ?\((?:b|c)Ram[0-9a-f]+ == '\\0'\) \{", s) or \
           re.match(r"if ?\(\*\(int \*\)\(_?[\w<>,`\[\]]+_TypeInfo \+ 0xe4\) == 0 ?\) \{", s) or re.match(r"if ?\(iRam0000000006d84d78 != 0\) \{", s) or \
           re.match(r"if ?\(pcRam[0-9a-f]+ == \(code \*\)0x0\) \{", s):
            e = block_end(i)
            if "pcRam" in s:   # keep the icall resolve as one line: pcRam = icall("...")
                inner = " ".join(x.strip() for x in lines[i + 1:e])
                mm = re.search(r"(pcRam[0-9a-f]+) = \(code \*\)(func_0x02d2a36c\(&UNK_[0-9a-f]+\));", inner)
                if mm: out.append(l[:len(l) - len(l.lstrip())] + mm.group(1) + " = " + mm.group(2) + ";")
            i = e + 1; continue
        if re.match(r"(?:b|c|u|i)Ram[0-9a-f]+ = (?:1|'\\x01'|0);$", s) or s.startswith("il2cpp_write_barrier(") or re.match(r"\w+ = il2cpp_write_barrier\(", s) or s.startswith("il2cpp_init_metadata(") or \
           s == "/* WARNING: Subroutine does not return */" or s.startswith("/* WARNING: Could not recover jumptable") or s.startswith("/* WARNING: Treating indirect jump") or \
           re.match(r"\w+ = il2cpp_init_metadata\(", s) or re.match(r"il2cpp_class_init\(\);", s) or re.match(r"func_0x02d65328\(\);", s):
            i += 1; continue
        if s.startswith("il2cpp_init_metadata") and not s.endswith(";"):   # multi-line call
            while i < len(lines) and not lines[i].rstrip().endswith(";"): i += 1
            i += 1; continue
        out.append(l)
        i += 1
    return "\n".join(out)

def join_wrapped(t):
    """Ghidra wraps long statements; put each statement back on one line (inside parentheses)."""
    out, buf, depth = [], None, 0
    for l in t.split("\n"):
        if buf is None:
            d = l.count("(") - l.count(")")
            if d > 0: buf, depth = l, d
            else: out.append(l)
        else:
            buf += " " + l.strip()
            depth += l.count("(") - l.count(")")
            if depth <= 0: out.append(buf); buf = None
    if buf is not None: out.append(buf)
    t = "\n".join(out)
    t = re.sub(r"\)\n\s+\(", ")(", t)
    t = re.sub(r"(=|&&|\|\||[-+*/,(])[ \t]*\n\s+", r"\1 ", t)
    # any statement still split over lines (a cast at the line end, a long condition): Ghidra statements end in ; { } or :
    out = []
    for l in t.split("\n"):
        s = l.rstrip()
        if out and not re.search(r"[;{}:]$|\*/$|^\s*/\*", out[-1]) and out[-1].strip() and not re.match(r"^\s*(else|do)\s*$", out[-1]):
            out[-1] = out[-1].rstrip() + " " + s.lstrip()
        else: out.append(s)
    t = "\n".join(out)
    t = re.sub(r"([\w>])\s+\(", r"\1(", t)
    t = re.sub(r"\(\s+", "(", t)
    t = re.sub(r",\s+", ", ", t)
    return t

def tidy(t):
    t = re.sub(r"\b(\w+) = new ([\w.<>,\[\]]+);\n\s*\1\.ctor\((.*?)\);", r"\1 = new \2(\3);", t)
    t = re.sub(r"\n[^\n]*if\(\(\*\(\w+ \+ 0x135\) & 1\) == 0\) \{\n[^\n]*\w+ = il2cpp_init_class\(\);\n[^\n]*\}", "", t)
    t = re.sub(r"\n[^\n]*if\(\*\(\w+ \+ 0xe4\) == 0\) \{\n[^\n]*\}", "", t)
    t = re.sub(r"\*\((method:[\w<>,.]+) \+ 0x38\)", r"\1.klass", t)
    t = re.sub(r"= new ([\w.<>\[\]]+)\((\w+), ([\w.<>]+\.(\w+))\);", lambda m: "= new %s(%s.%s);" % (m.group(1), m.group(2), m.group(4)), t)
    t = re.sub(r"\(\(?([\w.\[\]]+\.Length) & 0xfffffffe\)? != 0\)", r"(\1 >= 2)", t)
    t = re.sub(r"\(\(?([\w.\[\]]+\.Length) & 0xfffffffc\)? != 0\)", r"(\1 >= 4)", t)
    t = re.sub(r"\(\(?([\w.\[\]]+\.Length) & 0xfffffff8\)? != 0\)", r"(\1 >= 8)", t)
    t = re.sub(r"\(int\)([\w.]+\.Length)", r"\1", t)
    t = re.sub(r"\n[^\n]*if\(\(\w+ != (?:0|null)\) && \(\w+ = IsInst\(\w+, \*\(\*[\w.\[\]]+ \+ 0x40\)\), \w+ == (?:0|null)\)\)\s*goto LAB_\w+;", "", t)
    t = re.sub(r"if\(\(\w+ == (?:0|null)\) \|\| \(\w+ = IsInst\(\w+, \*\(\*[\w.\[\]]+ \+ 0x40\)\), \w+ != (?:0|null)\)\) \{", "{   // (array store type check)", t)
    t = re.sub(r"(\w+) = IsInst\(\w+, \*\(\*[\w.\[\]]+ \+ 0x40\)\);\n(\s*)if\(\1 != (?:0|null)\) \{", r"\2{   // (array store type check)", t)
    t = re.sub(r"\bthrow\(([^;]*?),\s*(?:0|method)\);", r"throw \1;", t)
    t = re.sub(r"\bthrow\(([^;]*?)\);", r"throw \1;", t)
    t = re.sub(r"il2cpp_throw_error\(\);", "throw new InvalidOperationException();   // index/cast check failed", t)
    t = re.sub(r"\(ulong\)", "", t)
    t = re.sub(r"\(\((\w+) & (?:1|0x1)\) != (?:0|0x0)\)", r"(\1)", t)
    t = re.sub(r"\(\((\w+) & (?:1|0x1)\) == (?:0|0x0)\)", r"(!\1)", t)
    t = re.sub(r"\(\((.+?) & (?:1|0x1)\) != (?:0|0x0)\)", r"(\1)", t)
    t = re.sub(r"\(bool\)", "", t)
    t = re.sub(r"!= 0\)", "!= null)", t) if False else t
    t = re.sub(r"\n{3,}", "\n\n", t)
    # indent 2 -> 4
    t = "\n".join(("  " + l if l.strip() else l) for l in t.split("\n"))
    return t

if __name__ == "__main__":
    XREF = open(os.environ["READABLE_XREF"], "w", encoding="utf-8") if os.environ.get("READABLE_XREF") else None
    load_strings(sys.argv[1])
    load_script(os.path.join(os.path.dirname(sys.argv[1]), "script.json"))
    load_so(sys.argv[2])
    raw_dir, out_dir = sys.argv[3], sys.argv[4]
    addr_index = {}
    for full, t in GAME.items():
        for m in t["methods"]:
            if m["addr"] > 0: addr_index["%x" % m["addr"]] = (full, m)
    os.makedirs(out_dir, exist_ok=True)
    files = sorted(os.listdir(raw_dir))
    if len(sys.argv) > 5: files = [f for f in files if f[:-2] in sys.argv[5:]]
    n = 0
    for f in files:
        if not f.endswith(".c"): continue
        addr = f[:-2]
        if addr not in addr_index: continue
        full, m = addr_index[addr]
        raw = open(os.path.join(raw_dir, f), encoding="utf-8", errors="replace").read()
        fn = Func(full, m, raw)
        try:
            res = fn.run()
        except Exception as e:
            res = Func(full, m, raw).emit_header() + "    /* readability pass failed: %s */\n" % e + raw
        if XREF is not None:
            for kind, target in sorted(fn.xref):
                XREF.write("%s\t%s\t%s\n" % (addr, kind, target))
        open(os.path.join(out_dir, addr + ".cs"), "w", encoding="utf-8").write(res)
        n += 1
    print("rewrote", n)
    if XREF is not None: XREF.close()
