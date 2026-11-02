# Ghidra (Jython): functions with exact bodies from methods.txt (va size name); labels for metadata globals and strings
import re
from ghidra.program.model.symbol import SourceType
from ghidra.program.model.address import AddressSet
from ghidra.app.cmd.disassemble import DisassembleCommand
args = getScriptArgs()
fm = currentProgram.getFunctionManager(); st = currentProgram.getSymbolTable()
sp = currentProgram.getAddressFactory().getDefaultAddressSpace()
def clean(n): return re.sub(r"[^A-Za-z0-9_$:<>.,`\[\]]", "_", n)[:200]
rows = [l.rstrip("\n").split("\t") for l in open(args[0])]
monitor.initialize(len(rows)); n = 0
for a, size, name in rows:
    monitor.incrementProgress(1)
    a = int(a, 16); size = int(size)
    start = sp.getAddress(a); end = sp.getAddress(a + size - 1)
    try:
        if currentProgram.getListing().getInstructionAt(start) is None:
            DisassembleCommand(start, AddressSet(start, end), True).applyTo(currentProgram, monitor)
        f = fm.getFunctionAt(start)
        if f is None:
            f = fm.createFunction(clean(name), start, AddressSet(start, end), SourceType.USER_DEFINED)
        else:
            f.setName(clean(name), SourceType.USER_DEFINED)
        if f is not None: n += 1
    except Exception as e:
        try: st.createLabel(start, clean(name), SourceType.USER_DEFINED)
        except: pass
print("functions %d of %d" % (n, len(rows)))
m = 0
for fn in (args[1], args[2]):
    for l in open(fn):
        a, name = l.rstrip("\n").split("\t")
        try: st.createLabel(sp.getAddress(int(a, 16)), clean(name), SourceType.USER_DEFINED); m += 1
        except: pass
print("labels %d" % m)
# il2cpp runtime helpers (identified from the generated code's patterns)
for a, name in ((0x2d2a3b0, "il2cpp_init_metadata"), (0x2d2a530, "il2cpp_class_init"), (0x2d2a64c, "il2cpp_object_new"),
                (0x2d2a354, "il2cpp_write_barrier"), (0x2d2a65c, "il2cpp_throw_null_reference"), (0x2d2a4a0, "il2cpp_array_new")):
    try:
        f = fm.getFunctionAt(sp.getAddress(a))
        if f is None: f = fm.createFunction(name, sp.getAddress(a), AddressSet(sp.getAddress(a), sp.getAddress(a + 3)), SourceType.USER_DEFINED)
        else: f.setName(name, SourceType.USER_DEFINED)
    except Exception as e: print("helper", name, e)
