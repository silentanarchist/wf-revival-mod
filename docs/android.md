# WF Revival for Android

*This guide was the README of the separate Android repository
([wf-revival-mod-android](https://github.com/silentanarchist/wf-revival-mod-android)) until both mods moved
into this one repository on 2026-10-17. The mod and the Warpforge Patcher app are released from this repository's
[releases page](https://github.com/silentanarchist/wf-revival-mod/releases) now; the old repository stays up so
older copies of the app can still find their update.*

Everything needed to run *Warhammer 40,000: Warpforge* on an Android phone against a
[WF Revival server](https://github.com/silentanarchist/wf-revival-server), now that
the game's own servers are closed:

- **the Android build of the WF Revival mod** (`WarpforgeRevival.Android.dll`),
- **the patcher** that puts a mod loader into your own copy of the Android game, and
- **the changes made to that mod loader** so it runs this game on current phones.

The Windows mod is in this same repository ([README](../README.md)).
Both are built from the same source and numbered `x.y.z.f-a` for Android and `x.y.z.f-w` for
Windows: `x.y.z` is the shared code and always the same on both; `f` counts fixes made for one of
them only, is left off while it is 0 (`0.12.3-a`), and starts again with every new `x.y.z`.

This is an unofficial fan project. It is not affiliated with or endorsed by Everguild or Games
Workshop. It contains no game files: you need your own copy of the game.

**About AI use.** The mod, the changes to the mod loader and patcher, the scripts and this
documentation were written largely by an AI assistant (Anthropic's Claude), working under the
maintainer's direction; the maintainer decided what to build and tested it on real phones. The
loader changes touch low-level code and have been tried on only a few devices, so expect rough
edges. Read the code before you rely on it, and please report what you find.

## Installing and making an account

The step-by-step guides, with screenshots, are on the Revival website, the one place they are kept up to
date. **The website needs an account:** sign in (or make a free account) at
[wf-revival.silentanarchist.com](https://wf-revival.silentanarchist.com/login) to read them.

- [Android device: the Warpforge Patcher app](https://wf-revival.silentanarchist.com/guides/phone) (the easiest way on Android)
- Android device with a PC and a cable: **archived** (the scripts are kept in `archive/pc-patcher/`, no longer updated); use the app above.
- [Android on a PC with MuMu Player](https://wf-revival.silentanarchist.com/guides/mumu)

No account yet? Make one on the [website](https://wf-revival.silentanarchist.com/login): you can read
the guides straight away, even while the account waits for approval. You can also make one in the game's own
sign-in window, which appears while the game loads. The same account works on PC and Android.

The rest of this page is about what is in this repository and how it is built.

---

## For server owners

A server needs two things for phones, both described in the
[server's README](https://github.com/silentanarchist/wf-revival-server):

- the Android build of the game's content files in `content/bundles-android/` (the content files
  differ per platform; the Windows ones do not load on a phone), and
- the Android mod as `mod/WarpforgeRevival.Android.dll`, so phones are offered updates.

---

## What is in this repository

| Path | What it is |
|---|---|
| `WarpforgeRevival/` | The mod's source. The same files build the Windows mod; the parts marked `ANDROID_PORT` are the Android differences. |
| `WarpforgeRevival.Android/` | The project that builds `WarpforgeRevival.Android.dll` from that source. |
| `releases/` | Built mods, Windows and Android. Each zip committed here is also published under Releases. |
| `archive/pc-patcher/` | **Archived (2026-10-28), no longer updated:** the old way of patching from a Windows PC with a cable (`1 - patch.bat`, `2 - install on phone.bat` and the other numbered scripts, the downloader for `LemonPatch.exe` and the loader package, and LemonPatch's source). Use the WF Revival Patcher app instead; see `ARCHIVED.md` there. |
| `loader-changes/` | The changes made to the mod loader itself, as diffs against the public sources. |

### The patching program

The WF Revival Patcher app and the archived `LemonPatch.exe` run the same code: LemonLoader's official patching code
([LemonLoader/MelonLoaderInstaller](https://github.com/LemonLoader/MelonLoaderInstaller), commit
`ac443fc`, GPL-3.0) with a command-line front end and two changes. Its complete source is the
[`warpforge-revival` branch of this project's fork](https://github.com/silentanarchist/MelonLoaderInstaller/tree/warpforge-revival):

- **One signing key for every run**, saved next to the tool, so a re-patched game installs over
  the previous one.
- **Heap pointer tagging switched off** in the game's manifest. Newer phones tag heap pointers,
  the loader's runtime drops the tag in places, and the system then closes the app.

It also keeps the game's own engine library instead of swapping in a generic one: the generic
build is for Unity 6000.2.6f1, the game's data is 6000.2.6f2, and the engine then refuses to load.

### The loader changes

The loader is [LemonLoader](https://github.com/LemonLoader/MelonLoader) 0.7 (the Android port of
MelonLoader) with [Il2CppInteropARM64](https://github.com/LemonLoader/Il2CppInteropARM64). Out of
the box it did not get this game to its menu on a current phone. The diffs in `loader-changes/`
are what it took. The complete changed sources are also published as the `warpforge-revival`
branch of this project's forks,
[silentanarchist/MelonLoader](https://github.com/silentanarchist/MelonLoader/tree/warpforge-revival) and
[silentanarchist/Il2CppInteropARM64](https://github.com/silentanarchist/Il2CppInteropARM64/tree/warpforge-revival):

`melonloader-lemonloader-0.7.diff` (against LemonLoader/MelonLoader `7b14dac3`)

- A native crash is handed on to the system instead of hanging the app: a small C handler
  (`faultprobe.c` in the diff) looks at every memory fault first, passes the ones in managed
  code on to the runtime, and for a fault in native code logs the address and the memory
  mappings around it, then lets Android write its full crash report. The crash watch that
  closes the game when the runtime's own report loops stays as a fallback.
- The runtime's memory settings (nursery size, how threads are stopped for the collector) are
  read from `UserData/runtime-gc.txt`, written by the mod.
- The loader's files are copied out of the app once per patch, not on every start (this was 30
  to 40 seconds of black screen each time).
- The game's name and versions are read from a small text file instead of the 700 MB data file.
- The short (4-byte) kind of hook is used only for functions too short for a normal one. Using
  it everywhere made the game fail at random on start. A function that already carries a hook is
  not taken for a short one.
- The interface files are taken ready-made from the package (see [The loader package](#the-loader-package)),
  because the tools that generate them do not run on a phone.

`il2cppinterop-arm64.diff` (against `f194da0` in the fork: LemonLoader/Il2CppInteropARM64 master
`a60ebf5` plus six fixes taken from [BepInEx/Il2CppInterop](https://github.com/BepInEx/Il2CppInterop))

- Text returned by the game's runtime is no longer freed by the loader (that crashed the app).
- The positions of six runtime functions are listed for this exact game build, because the
  searches that find them were written for Windows and found the wrong ones.
- Unused arguments, which arrive as garbage on ARM64, are checked before they are touched.
- Two functions the game calls tens of millions of times while loading get a shortcut in machine
  code. This took one loading step from 75 seconds to 6.

The function positions make this loader specific to **game version 1.35.0, 64-bit ARM**.

---

## The loader package

`melon_data.zip` is what the patcher puts into the game. It holds:

1. **The mod loader**, built from the public sources with the changes in `loader-changes/`.
2. **The .NET runtime** the loader runs on (Microsoft, MIT licence), and the small native
   libraries it needs (Dobby, OpenSSL, the C++ runtime).
3. **Interface files**: about 170 small libraries in `MelonLoader/Il2CppAssemblies` that describe
   the game's classes and functions so that a mod can call them. They are generated from the game
   by the loader's standard tools (Cpp2IL and Il2CppInterop). Normally that happens on the device
   at first start; those tools do not run on a phone for this game, so it was done on a PC and the
   result is shipped.

**The interface files contain no game code.** Every function in the game's own libraries is
reduced to a few generated lines that say "call the real function inside the game"; the real
function stays in your copy of the game and nowhere else. That was checked function by function
before publishing: of the 58,600 functions in the game's main library, all 58,600 are such
generated stubs, and none carries any of the game's text. What the files do carry is the *names*
of the game's classes, functions and fields.

The Unity engine libraries among them (`UnityEngine.*.dll`) are a different case: for the parts of
the engine the game's build leaves out, the loader's tools fill in Unity's own published library
code, as every MelonLoader installation does. That is Unity's engine code, not the game's.

## What is not here, and why

**The game.** Not ours to publish. You supply your own copy, and the patcher only works on it.

**Your signing key, your logs, ffmpeg, and anything in `game-files`, `patched`, `work-temp` or `phone-logs`.** They are
listed in `.gitignore` so they cannot be committed by accident.

---

## Building

### The mod

You need the .NET SDK (6 or newer) and reference libraries from your own installation. They are
not in this repository because they are not ours to publish.

| Folder | Where it comes from |
|---|---|
| `refs/net6` | the .NET 6 runtime libraries (`Microsoft.NETCore.App` 6.x) |
| `refs-android/ml` | `MelonLoader/net6` from the loader package (the pieces `1 - patch.bat` downloads into `archive\pc-patcher\tool`, joined, then unzipped) |
| `refs-android/il2cpp` | `MelonLoader/Il2CppAssemblies` from the loader package (the interface files) |

```
dotnet build WarpforgeRevival.Android/WarpforgeRevival.Android.csproj -c Release
```

The result is `WarpforgeRevival.Android/bin/Release/net6.0/WarpforgeRevival.Android.dll`.

### The loader

1. Check out the `warpforge-revival` branch of
   [silentanarchist/Il2CppInteropARM64](https://github.com/silentanarchist/Il2CppInteropARM64/tree/warpforge-revival)
   and build it: `dotnet build Il2CppInterop.HarmonySupport -c Release -p:VersionSuffix=arm64c`.
2. Check out the `warpforge-revival` branch of
   [silentanarchist/MelonLoader](https://github.com/silentanarchist/MelonLoader/tree/warpforge-revival).
   Make the Il2CppInterop build from step 1 available to it as a local NuGet source.
3. Build the native part with the .NET 9 SDK and the Android NDK (r27c):
   ```
   cd MelonLoader.Bootstrap
   dotnet publish -c Release -r linux-bionic-arm64 -p:DisableUnsupportedError=true -p:PublishAotUsingRuntimePack=true
   ```
   The result is `bin/Release/native/libmain.so`.

### The patching program

Check out the `warpforge-revival` branch of
[silentanarchist/MelonLoaderInstaller](https://github.com/silentanarchist/MelonLoaderInstaller/tree/warpforge-revival)
and publish `LemonPatch/LemonPatch.csproj` for `win-x64` as a single self-contained file. (Archived: the
PC tool is no longer updated. The copies of its source in `archive/pc-patcher/` are for reading; the project
file there expects the installer checked out next to that folder as `inst`. The app is built from
`WarpforgePatcher/` in the same branch, see its README.)

---

## Licences

Short version; [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) has the full list, the copyright
holders, the exact upstream commits and what was changed, and [`licenses/`](../licenses) has the
licence texts.

- **This project's own work** (the mod in `WarpforgeRevival/` and `WarpforgeRevival.Android/`,
  the scripts, this documentation): MIT, see [LICENSE](../LICENSE). It covers this project's work
  only, not the game.
- **`LemonPatch.exe`** and the files it is built from: GPL-3.0, as LemonLoader's installer is.
- **The loader in `melon_data.zip`**: MelonLoader / LemonLoader under Apache-2.0 and
  Il2CppInterop under LGPL-3.0, both changed by this project; the diffs are in
  `loader-changes/`.
- **Everything else in the loader package** (.NET runtime, HarmonyX, MonoMod, Cpp2IL, Dobby,
  OpenSSL and others) is unchanged and stays under its own licence.

## Thanks

None of this would exist without other people's work, given away freely:

- **The LemonLoader team** ([github.com/LemonLoader](https://github.com/LemonLoader)), who brought
  MelonLoader to Android, wrote the installer the patcher here is built on, and maintain the
  ARM64 version of Il2CppInterop. Running a Unity game's mods on a phone at all is their
  achievement; this project only adjusted their loader for one game.
- **LavaGang and the MelonLoader contributors** ([github.com/LavaGang/MelonLoader](https://github.com/LavaGang/MelonLoader)),
  for the mod loader both the Windows and the Android mod run on.
- **The BepInEx team**, for [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) and
  [HarmonyX](https://github.com/BepInEx/HarmonyX), which let a mod call into and patch the game.
- **Samboy and the Cpp2IL contributors** ([github.com/SamboyCoding/Cpp2IL](https://github.com/SamboyCoding/Cpp2IL)),
  whose tool reads the game's structure.
- **jmpews**, for [Dobby](https://github.com/jmpews/Dobby), the hooking library underneath.

If you use this, consider supporting those projects. And only get LemonLoader from
<https://github.com/LemonLoader>: there are look-alike download sites with the same name.
