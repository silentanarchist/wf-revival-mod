# Third-party notices

This repository contains this project's own work and, in `loader-changes/`, changes to other
people's software. The built programs (`LemonPatch.exe` and the loader package `melon_data.zip`) are
not stored here since 2026-10-17: `archive/pc-patcher/1 - patch.bat` downloads them into `archive/pc-patcher/tool/` from
[the commit of the old Android repository where they were published](https://github.com/silentanarchist/wf-revival-mod-android/tree/52b333dd265cbee4471db22bfe140d95e99754c5/patcher/tool),
and everything below applies to them as it did there. This file says what is whose, under which licence,
and what was changed. The licence texts are in [`licenses/`](licenses).

## This project's own work: MIT

The mod (`WarpforgeRevival/`, `WarpforgeRevival.Android/`), the scripts in `archive/pc-patcher/` and the
documentation are under the MIT licence in [LICENSE](LICENSE). That licence covers this project's
own work only. It does not cover the game, and it does not replace the licences below.

## Changed software

Each of these was changed by this project in October 2026. The complete changed source of each
is published as the `warpforge-revival` branch of this project's fork of it, and the changes
alone are also here as diffs.

| What | Where it is here | Licence | Complete source (fork), and what it is based on | Changes alone |
|---|---|---|---|---|
| **MelonLoader**, LemonLoader's Android port. Copyright 2020 - 2022 Lava Gang; Android port by the LemonLoader team. | Built into `archive/pc-patcher/tool/melon_data.zip` (`MelonLoader/`, `native/libmain.so`) | Apache-2.0 ([text](licenses/Apache-2.0.txt), [NOTICE](licenses/MelonLoader-NOTICE.txt)) | <https://github.com/silentanarchist/MelonLoader/tree/warpforge-revival> (`2e075e7f2407`), based on <https://github.com/LemonLoader/MelonLoader> branch `0.7.0` at `7b14dac3281fe9a88a1b8f0c96f5b9a46f558cdf` | [`loader-changes/melonloader-lemonloader-0.7.diff`](loader-changes/melonloader-lemonloader-0.7.diff) |
| **Il2CppInterop** (ARM64). By knah, BepInEx and contributors; ARM64 version by the LemonLoader team. | Built into `archive/pc-patcher/tool/melon_data.zip` (`MelonLoader/net6/Il2CppInterop.*.dll`) | LGPL-3.0 ([text](licenses/LGPL-3.0.txt), which adds to [GPL-3.0](licenses/GPL-3.0.txt)) | <https://github.com/silentanarchist/Il2CppInteropARM64/tree/warpforge-revival> (`abb023d18096`), based on <https://github.com/LemonLoader/Il2CppInteropARM64> master at `a60ebf5` plus six fixes from <https://github.com/BepInEx/Il2CppInterop> (together `f194da048db229ca6e649494095f9c9d3fce4e31`, in the fork) | [`loader-changes/il2cppinterop-arm64.diff`](loader-changes/il2cppinterop-arm64.diff) |
| **MelonLoader Installer** (LemonLoader). The patching program `LemonPatch.exe` is its patching code with a command-line front end. | `archive/pc-patcher/tool/LemonPatch.exe` (downloaded as `.part0`/`.part1`) | GPL-3.0 ([text](licenses/GPL-3.0.txt)) | <https://github.com/silentanarchist/MelonLoaderInstaller/tree/warpforge-revival> (`94f7a5ffbce5`), based on <https://github.com/LemonLoader/MelonLoaderInstaller> at `ac443fce9f2ef890caddf8c64bba9194a43361bd` | [`archive/pc-patcher/LemonPatch-core-change.diff`](archive/pc-patcher/LemonPatch-core-change.diff), [`archive/pc-patcher/LemonPatch-source.cs`](archive/pc-patcher/LemonPatch-source.cs), [`archive/pc-patcher/LemonPatch.csproj`](archive/pc-patcher/LemonPatch.csproj) |

`LemonPatch.exe` as a whole, including this project's front end and changes to it, is offered
under GPL-3.0. The changed Il2CppInterop libraries are offered under LGPL-3.0, and the changed
MelonLoader under Apache-2.0. How to rebuild each from source is in [docs/android.md](docs/android.md) under "Building".

If a fork above ever stops being available, open an issue here and the complete source of that
component will be published in this repository.

## Unchanged software inside the loader package

`archive/pc-patcher/tool/melon_data.zip` also carries these, unchanged, as MelonLoader ships them. The
package keeps MelonLoader's own notices in `MelonLoader/Documentation/` and the .NET runtime's in
`dotnet/LICENSE.txt` and `dotnet/ThirdPartyNotices.txt`.

| Component | Licence | Copyright / authors | Source |
|---|---|---|---|
| .NET runtime | MIT ([text](licenses/dotnet-runtime-LICENSE.txt)) | Copyright (c) .NET Foundation and Contributors | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.*, Microsoft.Bcl.AsyncInterfaces, Microsoft.Diagnostics.*, System.Configuration.ConfigurationManager and related | MIT | © Microsoft Corporation | <https://github.com/dotnet/runtime>, <https://github.com/dotnet/diagnostics>, <https://github.com/microsoft/clrmd> |
| HarmonyX (`0Harmony.dll`) | MIT | Copyright (c) 2020 BepInEx; Andreas Pardeike, Geoffrey Horsington, ManlyMarco et al. | <https://github.com/BepInEx/HarmonyX> |
| MonoMod (`MonoMod*.dll`) | MIT | Copyright 2022-2024 0x0ade, DaNike | <https://github.com/MonoMod/MonoMod> |
| Mono.Cecil | MIT | Jb Evain | <https://github.com/jbevain/cecil> |
| AsmResolver | MIT | Copyright © Washi 2016-2025 | <https://github.com/Washi1337/AsmResolver> |
| Cpp2IL (in the assembly generator) | MIT | Sam Byass (Samboy063) and contributors | <https://github.com/SamboyCoding/Cpp2IL> |
| Disarm | MIT | Copyright © Samboy063 2022 | <https://github.com/SamboyCoding/Disarm> |
| Tomlet | MIT | Sam Byass | <https://github.com/SamboyCoding/Tomlet> |
| WebSocketDotNet | MIT | Sam Byass (Samboy063) | <https://github.com/SamboyCoding/WebSocketDotNet> |
| Iced | MIT | Copyright (C) 2018-present iced project and contributors | <https://github.com/icedland/iced> |
| AssetRipper.Primitives, AssetRipper.VersionUtilities | MIT | Copyright (c) 2022-2024 ds5678 | <https://github.com/AssetRipper> |
| AssetsTools.NET | MIT | nesrak1 | <https://github.com/nesrak1/AssetsTools.NET> |
| Newtonsoft.Json | MIT | Copyright © James Newton-King 2008 | <https://github.com/JamesNK/Newtonsoft.Json> |
| IndexRange | MIT | Copyright 2019-2022 Bradley Grainger | <https://github.com/bgrainger/IndexRange> |
| bHapticsLib | MIT | Herp Derpinstine | <https://github.com/HerpDerpinstine/bHapticsLib> |
| Dobby (`native/libdobby.so`) | Apache-2.0 ([text](licenses/Apache-2.0.txt)) | jmpews | <https://github.com/jmpews/Dobby> |
| OpenSSL 3.3.1 (`native/libssl.so`, `native/libcrypto.so`) | Apache-2.0 ([text](licenses/Apache-2.0.txt)) | The OpenSSL Project Authors | <https://github.com/openssl/openssl> |
| LLVM libc++ (`native/libc++_shared.so`, from the Android NDK) | Apache-2.0 with LLVM exceptions | The LLVM Project | <https://github.com/llvm/llvm-project> |

Other libraries MelonLoader builds in from source (TinyJSON, SharpZipLib, Semver, mgGif, JNISharp,
plthook and others) are listed with their licences in MelonLoader's own README:
<https://github.com/LemonLoader/MelonLoader#licensing--credits>.

The MIT licence, which the MIT-licensed components above share (each with its own copyright line
as given in the table):

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software
> and associated documentation files (the "Software"), to deal in the Software without
> restriction, including without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
> Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
> BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
> DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Not covered by any licence here

- **The game.** Nothing in this repository gives any right to *Warhammer 40,000: Warpforge*, its
  files or its artwork. You need your own copy.
- **Interface files** in the loader package (`MelonLoader/Il2CppAssemblies`) are generated from
  the game by the tools above and describe the game's classes by name; see the README.
- **Unity engine libraries** among those files contain Unity Technologies' own library code, as
  every MelonLoader installation produces. They remain Unity's.
