@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - add LemonLoader (0.7 test build)

echo.
echo  Warpforge for Android - add LemonLoader (0.7 test build)
echo  ---------------------------------------
echo.

rem ---- 0. download the patching tool and the loader if they are not here yet ----
rem (about 84 MB; tool\get-tools.ps1 checks every piece against its SHA-256)
if exist "tool\LemonPatch.exe" if exist "tool\melon_data.zip" goto :havetools
powershell -NoProfile -ExecutionPolicy Bypass -File "tool\get-tools.ps1"
if errorlevel 1 goto :nodownload
:havetools

rem ---- 1. the patching tool (delivered in two halves, joined on every run) ----
if exist "tool\LemonPatch.exe.part0" (
    if not exist "tool\LemonPatch.exe.part1" goto :notool
    copy /b /y "tool\LemonPatch.exe.part0"+"tool\LemonPatch.exe.part1" "tool\LemonPatch.exe" >nul
)
if not exist "tool\LemonPatch.exe" goto :notool

rem ---- 2. the game's own files ----
set "PACK="
for %%F in (*.xapk *.apks) do set "PACK=%%F"
if exist "game-files\*.apk" goto :havefiles
if not defined PACK goto :nogame
echo  Unpacking "%PACK%" ...
if not exist "game-files" mkdir "game-files"
tar -xf "%PACK%" -C "game-files"
if errorlevel 1 goto :unpackfailed
:havefiles
if not exist "game-files\UnityDataAssetPack.apk" (
    echo.
    echo  NOTE: game-files has no UnityDataAssetPack.apk. That is the game's big data
    echo  file ^(about 700 MB^). Without it the patched game will not start.
    echo.
)

rem ---- 3. the loader itself: a test build of LemonLoader 0.7, delivered in pieces ----
if exist "tool\melon_data.zip.part0" (
    if not exist "tool\melon_data.zip.part1" goto :nodownload
echo.
echo  The patching tool and the loader could not be downloaded. Check the internet
echo  connection and run this again, or download these five files yourself from
echo    https://github.com/silentanarchist/wf-revival-mod-android/tree/52b333dd265cbee4471db22bfe140d95e99754c5/patcher/tool
echo  and put them in the "tool" folder:
echo    LemonPatch.exe.part0, LemonPatch.exe.part1,
echo    melon_data.zip.part0, melon_data.zip.part1, melon_data.zip.part2
goto :end
:noloader
    if exist "tool\melon_data.zip.part2" (
        copy /b /y "tool\melon_data.zip.part0"+"tool\melon_data.zip.part1"+"tool\melon_data.zip.part2" "tool\melon_data.zip" >nul
    ) else (
        copy /b /y "tool\melon_data.zip.part0"+"tool\melon_data.zip.part1" "tool\melon_data.zip" >nul
    )
)
if not exist "tool\melon_data.zip" goto :nodownload
echo.
echo  The patching tool and the loader could not be downloaded. Check the internet
echo  connection and run this again, or download these five files yourself from
echo    https://github.com/silentanarchist/wf-revival-mod-android/tree/52b333dd265cbee4471db22bfe140d95e99754c5/patcher/tool
echo  and put them in the "tool" folder:
echo    LemonPatch.exe.part0, LemonPatch.exe.part1,
echo    melon_data.zip.part0, melon_data.zip.part1, melon_data.zip.part2
goto :end
:noloader

rem ---- 4. patch ----
echo  Patching. The big data file takes a few minutes; please wait.
echo.
"tool\LemonPatch.exe" "game-files" "patched" "tool\melon_data.zip"
if errorlevel 1 goto :failed

echo.
echo  ------------------------------------------------------------
echo   Done. The patched game is in the "patched" folder:
echo.
dir /b "patched\*.apk"
echo.
echo   Next: run "2 - install on phone.bat", or see READ ME.txt.
echo.
echo   These files are signed with the key kept in tool\signing-key.pem, so they
echo   install over an earlier copy patched with this same folder. A copy patched
echo   before that key existed has to be uninstalled from the phone once first.
echo  ------------------------------------------------------------
echo.
pause
exit /b 0

:notool
echo  The patching tool is missing. The "tool" folder should contain
echo  LemonPatch.exe.part0 and LemonPatch.exe.part1.
goto :end
:nogame
echo  The game's files are not here yet. Do ONE of these, then run this again:
echo.
echo    a) put your backup of the game (one .apks or .xapk file) in this folder
echo         %~dp0
echo    b) or put its three .apk files in the "game-files" folder:
echo         com.Everguild.WarhammerWarpforge.apk
echo         config.arm64_v8a.apk
echo         UnityDataAssetPack.apk
echo.
echo  The game has to be version 1.35.0.
if not exist "game-files" mkdir "game-files"
goto :end
:unpackfailed
echo  Could not unpack "%PACK%". Open it with 7-Zip and copy the .apk files
echo  inside it into the "game-files" folder, then run this again.
goto :end
:nodownload
echo.
echo  The patching tool and the loader could not be downloaded. Check the internet
echo  connection and run this again, or download these five files yourself from
echo    https://github.com/silentanarchist/wf-revival-mod-android/tree/52b333dd265cbee4471db22bfe140d95e99754c5/patcher/tool
echo  and put them in the "tool" folder:
echo    LemonPatch.exe.part0, LemonPatch.exe.part1,
echo    melon_data.zip.part0, melon_data.zip.part1, melon_data.zip.part2
goto :end
:noloader
echo  The loader files are missing. The "tool" folder should contain
echo  melon_data.zip.part0, melon_data.zip.part1 and melon_data.zip.part2.
goto :end
:failed
echo.
echo  Patching did not finish. The details are in patch-log.txt in this folder.
goto :end
:end
echo.
pause
exit /b 1
