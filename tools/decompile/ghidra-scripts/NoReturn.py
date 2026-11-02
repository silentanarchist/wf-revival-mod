import re
fm = currentProgram.getFunctionManager()
n = 0
pat = re.compile(r"(^il2cpp_throw_null_reference$|^il2cpp_raise|Throw[A-Z]\w*Exception|\$\$Throw|ThrowHelper\$\$|\$\$ThrowIf|_ThrowHelper__Throw)")
for f in fm.getFunctions(True):
    if pat.search(f.getName()):
        f.setNoReturn(True); n += 1
print("noreturn set on %d functions" % n)
