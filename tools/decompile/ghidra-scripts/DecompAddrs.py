# decompile the functions at the given addresses (creating them if Ghidra has none there); args: <addr list file> <out file>
import codecs
from ghidra.app.decompiler import DecompInterface, DecompileOptions
from ghidra.util.task import ConsoleTaskMonitor
from ghidra.app.cmd.function import CreateFunctionCmd
from ghidra.app.cmd.disassemble import DisassembleCommand
args = getScriptArgs()
fm = currentProgram.getFunctionManager(); sp = currentProgram.getAddressFactory().getDefaultAddressSpace()
ifc = DecompInterface(); ifc.setOptions(DecompileOptions()); ifc.openProgram(currentProgram)
out = codecs.open(args[1], "w", "utf-8")
for l in open(args[0]):
    a = sp.getAddress(int(l.strip(), 16))
    f = fm.getFunctionAt(a)
    if f is None:
        if currentProgram.getListing().getInstructionAt(a) is None: DisassembleCommand(a, None, True).applyTo(currentProgram, monitor)
        CreateFunctionCmd(a).applyTo(currentProgram, monitor); f = fm.getFunctionAt(a)
    if f is None: out.write("/* ==== %s: no function */\n" % l.strip()); continue
    res = ifc.decompileFunction(f, 60, ConsoleTaskMonitor())
    body = res.getDecompiledFunction().getC() if res.decompileCompleted() else "/* failed */"
    out.write("/* ==== %s (%d bytes) */\n%s\n" % (l.strip(), f.getBody().getNumAddresses(), "\n".join(x for x in body.split("\n") if not x.startswith("/* WARNING"))[:3000]))
out.close()
