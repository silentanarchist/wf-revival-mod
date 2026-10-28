@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - put the mod on the phone

echo.
echo  Warpforge for Android - put the mod on the phone
echo  -----------------------------------------------------
echo.
rem the mod's file: WFRevival.Android.dll from 0.15.0 (the rename), WarpforgeRevival.Android.dll before
set "MOD="
if exist "mods\WarpforgeRevival.Android.dll" set "MOD=WarpforgeRevival.Android.dll"
if exist "mods\WFRevival.Android.dll" set "MOD=WFRevival.Android.dll"
if not defined MOD (
    echo  The mod ^(WFRevival.Android.dll^) is missing from the "mods" folder.
    goto :end
)
rem where it goes on the phone: under its own name, or - for WFRevival.Android.dll - under the old name the way a
rem 0.14.x updater puts it, to test the mod moving itself (it moves at the next start of the game)
set "TARGET=%MOD%"
set "OTHER=WFRevival.Android.dll"
if "%MOD%"=="WFRevival.Android.dll" set "OTHER=WarpforgeRevival.Android.dll"
if not "%MOD%"=="WFRevival.Android.dll" goto picked
echo  WFRevival.Android.dll: put it on the phone as
echo    N = WFRevival.Android.dll ^(a normal install^)
echo    O = WarpforgeRevival.Android.dll, the old name, like a 0.14.x updater does - to test the mod moving itself
choice /C NO /M " New or Old name"
if errorlevel 2 (
    set "TARGET=WarpforgeRevival.Android.dll"
    set "OTHER=WFRevival.Android.dll"
)
:picked

set "ADB="
if exist "tool\platform-tools\adb.exe" set "ADB=%~dp0tool\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"
if not defined ADB (
    echo  This needs Google's "platform-tools" ^(the adb program^).
    echo  Download "SDK Platform-Tools for Windows" from
    echo    https://developer.android.com/tools/releases/platform-tools
    echo  and unzip it so that this file exists:
    echo    %~dp0tool\platform-tools\adb.exe
    echo  Then run this again. ^(Or install from the phone itself - see READ ME.txt.^)
    goto :end
)

echo  Phone connected by USB, with "USB debugging" switched on?
echo  Devices found:
"%ADB%" devices
echo.

rem ---- choose the phone: the first real device that is ready (emulators only if nothing else) ----
setlocal enabledelayedexpansion
set "DEV="
for /f "skip=1 tokens=1,2" %%A in ('""%ADB%" devices"') do (
    set "N=%%A"
    if "%%B"=="device" if /i not "!N:~0,9!"=="emulator-" if not defined DEV set "DEV=%%A"
)
if not defined DEV for /f "skip=1 tokens=1,2" %%A in ('""%ADB%" devices"') do if "%%B"=="device" if not defined DEV set "DEV=%%A"
if not defined DEV (
    echo  No phone is ready. Check the cable, that USB debugging is on, and that the
    echo  "Allow USB debugging?" prompt on the phone was accepted. A phone listed as
    echo  "unauthorized" or "offline" is not ready yet.
    goto :end
)
echo  Using device: !DEV!
echo.
set "P=/sdcard/MelonLoader/com.Everguild.WarhammerWarpforge"
"%ADB%" -s !DEV! shell mkdir -p %P%/Mods
rem the earlier test build must not load next to the real mod: it is moved aside, not deleted
"%ADB%" -s !DEV! shell "mkdir -p %P%/Mods-disabled; if [ -f %P%/Mods/WarpforgeRevival.AndroidTest.dll ]; then mv %P%/Mods/WarpforgeRevival.AndroidTest.dll %P%/Mods-disabled/; fi"
rem the retired OpenGL graphics plugin is moved aside too (the game then draws the way it chooses itself)
"%ADB%" -s !DEV! shell "mkdir -p %P%/Plugins-disabled; if [ -f %P%/Plugins/WarpforgeRevival.AndroidGraphics.dll ]; then mv %P%/Plugins/WarpforgeRevival.AndroidGraphics.dll %P%/Plugins-disabled/; fi"
rem only one copy of the mod on the phone: the other name is removed
"%ADB%" -s !DEV! shell "rm -f %P%/Mods/!OTHER!"
"%ADB%" -s !DEV! push "mods\!MOD!" %P%/Mods/!TARGET!
if errorlevel 1 (
    echo.
    echo  Copying failed. Has the patched game been started at least once on this phone?
    goto :end
)
echo.
echo  The mod is on the phone. Mods folder now holds:
"%ADB%" -s !DEV! shell ls -la %P%/Mods
echo.
echo  Next: run "0 - get phone logs.bat" and start the game when it asks.

:end
echo.
pause
