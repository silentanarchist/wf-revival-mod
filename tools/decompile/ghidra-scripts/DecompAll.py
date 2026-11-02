# Ghidra (Jython) post-script: decompile every function in funcs.tsv (addr \t class \t method) on N threads.
# args: <funcs.tsv> <out dir> <threads>   Output: <out dir>/<addr>.c (skips files that already exist, so it can resume)
import os, codecs, threading, time
codecs.lookup("utf-8"); codecs.lookup("utf_8")
from ghidra.app.decompiler import DecompInterface, DecompileOptions
from ghidra.util.task import ConsoleTaskMonitor
from java.util.concurrent import Executors, TimeUnit
args = getScriptArgs()
rows = [l.rstrip("\n").split("\t") for l in open(args[0]) if l.strip()]
out = args[1]; nthreads = int(args[2])
if not os.path.isdir(out): os.makedirs(out)
fm = currentProgram.getFunctionManager()
sp = currentProgram.getAddressFactory().getDefaultAddressSpace()
todo = [r for r in rows if not os.path.exists(os.path.join(out, r[0] + ".c"))]
print("to decompile: %d of %d" % (len(todo), len(rows)))
lock = threading.Lock()
state = {"done": 0, "fail": 0, "t0": time.time()}
local = threading.local()

def get_ifc():
    ifc = getattr(local, "ifc", None)
    if ifc is None:
        ifc = DecompInterface(); opts = DecompileOptions(); ifc.setOptions(opts); ifc.openProgram(currentProgram)
        ifc.setSimplificationStyle("decompile"); local.ifc = ifc
    return ifc

def work(row):
    addr, cls, meth = row
    try:
        f = fm.getFunctionAt(sp.getAddress(int(addr, 16)))
        if f is None:
            body = "/* no function at this address */\n"
        else:
            res = get_ifc().decompileFunction(f, int(args[3]) if len(args) > 3 else 90, ConsoleTaskMonitor())
            if res.decompileCompleted():
                body = res.getDecompiledFunction().getC()
            else:
                body = "/* not decompiled: %s */\n" % res.getErrorMessage()
        lines = [l for l in body.split("\n") if not l.startswith("/* WARNING")]
        fh = codecs.open(os.path.join(out, addr + ".c.tmp"), "w", "utf-8")
        fh.write("/* ---- %s::%s  at 0x%s, %d bytes */\n" % (cls, meth, addr.upper(), f.getBody().getNumAddresses() if f else 0))
        fh.write("\n".join(lines)); fh.close()
        os.rename(os.path.join(out, addr + ".c.tmp"), os.path.join(out, addr + ".c"))
    except Exception as e:
        with lock: state["fail"] += 1
        print("fail %s %s" % (addr, e))
    with lock:
        state["done"] += 1
        if state["done"] % 500 == 0:
            el = time.time() - state["t0"]
            print("%d done, %d failed, %.0f s, %.1f/s" % (state["done"], state["fail"], el, state["done"] / el))

pool = Executors.newFixedThreadPool(nthreads)
for r in todo:
    pool.submit(lambda r=r: work(r))
pool.shutdown()
while not pool.awaitTermination(60, TimeUnit.SECONDS):
    pass
print("finished: %d done, %d failed, %.0f s" % (state["done"], state["fail"], time.time() - state["t0"]))
