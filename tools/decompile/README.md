# Decompiling the game into readable pseudo-code

The scripts that turn the game's compiled code into a readable, project-like tree of C#-flavoured pseudo-code:
one file per type, real names for types, methods, fields, parameters, enum values and strings, field accesses
rebuilt from memory offsets, IL2CPP bookkeeping removed, and (for the battle code) every local variable named and
a one-line summary above each method. It is for reading and for finding hook points; it is not compilable and not
meant to be.

**Nothing here contains game code or game content.** You run it against your own copy of the game. The output
is the game's code and stays private: keep it out of repositories and don't post it.

What you need: Linux or WSL with Python 3, Java 21, [Ghidra 11.4](https://github.com/NationalSecurityAgency/ghidra/releases)
and [Il2CppDumper](https://github.com/Perfare/Il2CppDumper); about 2 GB of disk and an hour of machine time
(2 cores; faster with more).

## 1. Dump the metadata

From your own install, take the Android build's `libil2cpp.so` (arm64-v8a, from the APK/XAPK) and
`global-metadata.dat` (in `assets/bin/Data/Managed/Metadata/`). The PC build is the same code, but the Android
binary decompiles better. Run Il2CppDumper on them; you get `dump.cs`, `script.json`, `stringliteral.json`,
`il2cpp.h` and `DummyDll/`.

## 2. Build the type model and the lists

```
python3 parse_dump.py dump.cs model-all.json
python3 parse_dump.py dump.cs model.json Assembly-CSharp.dll Assembly-CSharp-firstpass.dll
python3 make_lists.py script.json model.json model-all.json lists/
```

`model.json` is the game's own types (the two images above); `model-all.json` is every type (needed for the
fields of Unity/.NET base classes). `lists/` gets `methods.txt`, `metadata.txt`, `strings.txt` (names for
Ghidra), `sigs.tsv` (parameter names and types) and `funcs.tsv` (what to decompile).

## 3. Ghidra: import, name, decompile

```
G=/path/to/ghidra_11.4/support/analyzeHeadless
$G proj wf -import libil2cpp.so -loader-imagebase 0 -processor AARCH64:LE:64:v8A -noanalysis
$G proj wf -process libil2cpp.so -noanalysis -scriptPath ghidra-scripts \
   -postScript ApplyNames.py lists/methods.txt lists/metadata.txt lists/strings.txt
$G proj wf -process libil2cpp.so -noanalysis -scriptPath ghidra-scripts -postScript NoReturn.py
$G proj wf -process libil2cpp.so -noanalysis -scriptPath ghidra-scripts -postScript SetSigs.py lists/sigs.tsv
MAXMEM=4G $G proj wf -process libil2cpp.so -noanalysis -readOnly -scriptPath ghidra-scripts \
   -postScript DecompAll.py lists/funcs.tsv raw/ 2 90
```

`ApplyNames.py` creates a function with its real name for every method (and labels for the metadata globals and
string literals; the six IL2CPP runtime helpers at the end of the script are addresses of the 1.35.0 Android
build - find them again for another build). `NoReturn.py` marks the throw helpers as non-returning so the
decompiler doesn't produce garbage after them. `SetSigs.py` gives every method its real parameter names.
`DecompAll.py <list> <out> <threads> <timeout s>` writes `raw/<address>.c` per function; it skips files that exist,
so it can be stopped and restarted. The whole game (23,900 functions) took 35 minutes on 2 cores; the few that
time out can be re-run from a shorter list with a 600 s timeout.

## 4. Make it readable and assemble the tree

```
python3 readable.py stringliteral.json libil2cpp.so raw/ readable/
python3 assemble.py readable/ game-code/ folders.json
```

`readable.py` (3 minutes) does the renaming and cleanup described above. The table `HELPERS` near the top holds
the addresses of a few runtime helpers Il2CppDumper does not name; they are for the 1.35.0 Android build. For
another build, look for frequent `il2cpp_runtime_...` calls in the output, decompile those addresses with
`DecompAddrs.py` and fill the table in. `assemble.py` builds the tree: `folders.json` is the area each type goes
to (by type name; types it doesn't know get a folder from a type with the same name prefix, or `Other/`),
`INDEX.md` lists every file, `methods.tsv` maps every method address to its file.

## 5. (Optional) names chosen by reading each function

`rename-maps/` holds the variable names and one-line summaries an AI chose for the 2,751 functions of `Battle/`,
`Cards/` and `Decks/` of 1.35.0, keyed by method address. Apply them with no AI involved:

```
python3 apply_names.py readable/ rename-maps/
python3 assemble.py readable/ game-code/ folders.json
```

To do the same for another area, `make_batches.py game-code/ readable/ batches/ Menu/` writes batch files of
~130k characters; give each to an AI with `naming-prompt.txt` (replace BATCHFILE and OUTFILE), collect the JSON
files into a maps folder and apply them the same way. Only decompiler-made names (`uVar3`, `lVar8`, `uStack_48`,
`this_00`, `param_3`, `unaff_x19`...) are ever renamed, and only to names not already used in the function.

## Reading the output

`assemble.py` copies `tree-README.md` into `game-code/README.md`; it explains how to read the
files: field offsets, `obj.field` from offsets, `// interface call`, coroutine state machines, the compiler's
null checks, what `str_...` identifiers and `*(x + 0x38)` leftovers mean.

## 6. (Optional) the website's code browser

The site can show the readable tree to accounts with the Developer switch (server: Revival/Code*, page /#code).
It needs the cross-references, which `readable.py` writes when `READABLE_XREF` names a file:

```
READABLE_XREF=xref.tsv python3 readable.py stringliteral.json libil2cpp.so raw/ /tmp/discard/
python3 build_code_index.py game-code/ xref.tsv code-data/ Battle Cards Decks
```

(the readable output of that run can be thrown away; the cross-references do not depend on the AI-chosen names).
`build_code_index.py` copies the served folders and writes `index.json` (files, types, methods with line ranges,
fields, the search's name list, the cross-references). Copy `code-data/` to the server's `data/code/` (never under
`content/files`); the server reads it again when `index.json` changes. Serve more folders by adding them to the list.
