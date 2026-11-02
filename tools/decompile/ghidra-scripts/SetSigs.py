# Ghidra (Jython) post-script: apply parameter names and simple types from sigs.tsv (addr \t ret \t "type name,...")
import sys
from ghidra.program.model.symbol import SourceType
from ghidra.program.model.listing import ParameterImpl, ReturnParameterImpl, Function
from ghidra.program.model.data import IntegerDataType, UnsignedIntegerDataType, BooleanDataType, FloatDataType, DoubleDataType, \
    LongLongDataType, UnsignedLongLongDataType, ByteDataType, SignedByteDataType, ShortDataType, UnsignedShortDataType, VoidDataType
T = {"int": IntegerDataType.dataType, "uint": UnsignedIntegerDataType.dataType, "bool": BooleanDataType.dataType,
     "float": FloatDataType.dataType, "double": DoubleDataType.dataType, "longlong": LongLongDataType.dataType,
     "ulonglong": UnsignedLongLongDataType.dataType, "byte": ByteDataType.dataType, "sbyte": SignedByteDataType.dataType,
     "short": ShortDataType.dataType, "ushort": UnsignedShortDataType.dataType, "void": VoidDataType.dataType}
fm = currentProgram.getFunctionManager()
sp = currentProgram.getAddressFactory().getDefaultAddressSpace()
rows = [l.rstrip("\n").split("\t") for l in open(getScriptArgs()[0]) if l.strip()]
monitor.initialize(len(rows)); n = 0; bad = 0
for addr, ret, params in rows:
    monitor.incrementProgress(1)
    f = fm.getFunctionAt(sp.getAddress(int(addr, 16)))
    if f is None: bad += 1; continue
    try:
        ps = []
        for p in params.split(","):
            if not p.strip(): continue
            ty, name = p.strip().split(" ", 1)
            while name in [x.getName() for x in ps]: name = name + "_"
            ps.append(ParameterImpl(name, T[ty], currentProgram))
        f.updateFunction(None, ReturnParameterImpl(T[ret], currentProgram), ps,
                         Function.FunctionUpdateType.DYNAMIC_STORAGE_FORMAL_PARAMS, True, SourceType.USER_DEFINED)
        n += 1
    except:
        bad += 1
        e = sys.exc_info()[1]
        if bad < 10: print("sig", addr, e)
print("signatures set %d, failed/missing %d" % (n, bad))
