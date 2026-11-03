#!/usr/bin/env python3
"""readable.py for functions decompiled from the PC build (GameAssembly.dll, x64).
Usage: readable_pc.py <pc dump dir (script.json, stringliteral.json)> <raw dir> <out dir>
Needs pc-model/model.json and pc-model/model-all.json (parse_dump.py on the PC dump.cs), read through READABLE_MODEL.

The same readability pass as readable.py; before it runs, the x64 decompiler output is rewritten into the shapes
the pass knows from the Android build: function names from the PC script.json (which lists RVAs; the Ghidra
program is at 0x180000000), the x64 IL2CPP helpers (HELPERS_PC below, for game 1.35.0 - find them again for
another build by decompiling the frequent FUN_/func_0x addresses with DecompAddrs.py), MSVC's lazy
"if (DAT_x == 0) { init(&X_TypeInfo); DAT_x = 1; }" metadata guards, the outlined static-field getters
(FUN_x() that returns *(X_TypeInfo + 0xb8) + off), and the int3 after a null-check throw.
Output files are named by the PC code address (VA hex) and get the usual `// 0x...` header for assemble.py."""
import json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
os.environ.setdefault("READABLE_MODEL", os.path.join(HERE, "pc-model"))
import readable
from readable import Func, ghidra_c

BASE = 0x180000000

HELPERS_PC = {
    "FUN_1803f44f0": "CLASSINIT",                 # il2cpp_codegen_initialize_runtime_metadata / class init (lazy, guarded)
    "func_0x0001803f4830": "il2cpp_runtime_class_init",
    "FUN_1803f47a0": "il2cpp_throw_null_reference",   # followed by int3 (swi(3)) - removed below
    "func_0x0001803f3730": "BARRIER",             # GC write barrier
    "thunk_FUN_18046e1a0": "BARRIER",
    "FUN_180482360": "memset",
    "func_0x0001803f4750": "il2cpp_object_new",
    "FUN_1803f4800": "icall",                     # il2cpp_resolve_icall
}

STATIC_GETTERS = {}   # FUN_x -> (TypeInfo name, offset): outlined "return *(X_TypeInfo + 0xb8) + off" getters

def load_pc_names(dump_dir):
    for e in json.load(open(os.path.join(dump_dir, "script.json")))["ScriptMethod"]:
        n = e["Name"]
        i = n.find("$$")
        if i < 0: continue
        key = "func_0x%012x" % (e["Address"] + BASE)
        readable.FUNC_NAMES[key] = ghidra_c(n[:i]) + "__" + re.sub(r"[^\w<>`]", "_", n[i + 2:])

GUARD = re.compile(r"\n[ \t]*if \((?:_?DAT_[0-9a-f]+|\w+) == '\\0'\) \{\s*\n(?:[ \t]*CLASSINIT\([^;]*\);\s*\n)+[ \t]*(?:_?DAT_[0-9a-f]+|\w+) = '\\x01';\s*\n[ \t]*\}")
INT3 = re.compile(r"\n[ \t]*(\w+) = \(code \*\)swi\(3\);\s*\n[ \t]*\(\*\1\)\(\);")

def strip_barrier(t):
    """the GC write barrier the x64 build inlines: if (iRam...184558c38 != 0) { ...card table update... }"""
    out = []
    i = 0
    while True:
        m = re.search(r"\n[ \t]*if \(iRam0*184558c38 != 0\) \{", t[i:])
        if not m: out.append(t[i:]); break
        start = i + m.start()
        out.append(t[i:start])
        j = i + m.end(); depth = 1
        while depth and j < len(t):
            if t[j] == "{": depth += 1
            elif t[j] == "}": depth -= 1
            j += 1
        i = j
    return "".join(out)

def normalise(t, getters):
    t = strip_barrier(t)
    t = re.sub(r"\blonglong\b", "long", t).replace("ulonglong", "ulong")
    t = re.sub(r"\b(0x[0-9a-fA-F]+|\d+)U\b", r"\1", t)          # unsigned suffixes: param_1 + 0x48U
    for fn, nm in HELPERS_PC.items(): t = t.replace(fn, nm)
    for fn, (ti, off) in getters.items():
        t = re.sub(r"\b%s\(0?\)" % fn, "*(*(longlong *)(%s + 0xb8) + %s)" % (ti, off) if off else "**(%s + 0xb8)" % ti, t)
    t = GUARD.sub("", t)
    t = INT3.sub("", t)
    t = re.sub(r"\bfunc_0x0*([0-9a-f]{9})\b", lambda m: readable.FUNC_NAMES.get("func_0x%012x" % int(m.group(1), 16), "il2cpp_runtime_" + m.group(1)), t)
    t = re.sub(r"\b_?DAT_1845[0-9a-f]{5}\b", "il2cpp_static_DAT", t)
    return t

GUARD2 = re.compile(r"\n[ \t]*if\(il2cpp_static_DAT == '\\0'\) \{\n(?:[ \t]*(?:\w+ = 0x18[0-9a-f]+;|CLASSINIT\([^;]*\) ?;)\n)*[ \t]*il2cpp_static_DAT = '\\x01';\n[ \t]*\}")
INIT2 = re.compile(r"\n[ \t]*if\(\*\(il2cpp_static_DAT \+ 0xe4\) == 0\) \{\n[ \t]*il2cpp_runtime_class_init\(\);\n[ \t]*\}")

def post(t):
    """leftovers of the MSVC lazy-init guards after the pass"""
    t = GUARD2.sub("", t)
    t = INIT2.sub("", t)
    return t

if __name__ == "__main__":
    dump_dir, raw_dir, out_dir = sys.argv[1:4]
    readable.load_strings(os.path.join(dump_dir, "stringliteral.json"))
    load_pc_names(dump_dir)
    pc_types = readable.GAME
    addr_index = {}
    for full, t in pc_types.items():
        for m in t["methods"]:
            if m["addr"] > 0:
                m = dict(m, addr=m["addr"] + BASE)
                addr_index["%x" % m["addr"]] = (full, m)
    getters = json.load(open(os.path.join(HERE, "pc-static-getters.json"))) if os.path.exists(os.path.join(HERE, "pc-static-getters.json")) else {}
    os.makedirs(out_dir, exist_ok=True)
    n = 0
    for f in sorted(os.listdir(raw_dir)):
        if not f.endswith(".c") or f[:-2] not in addr_index: continue
        full, m = addr_index[f[:-2]]
        raw = normalise(open(os.path.join(raw_dir, f), encoding="utf-8", errors="replace").read(), getters)
        fn = Func(full, m, raw)
        try: res = post(fn.run())
        except Exception as e: res = Func(full, m, raw).emit_header() + "    /* readability pass failed: %s */\n" % e + raw
        open(os.path.join(out_dir, f[:-2] + ".cs"), "w", encoding="utf-8").write(res)
        n += 1
    print("rewrote", n)
