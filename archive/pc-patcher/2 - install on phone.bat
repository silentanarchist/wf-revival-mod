@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - install on phone

echo.
echo  Warpforge for Android - install the patched game on a phone
echo  -----------------------------------------------------------
echo.
if not exist "patched\base.apk" (
    echo  There is no patched game yet. Run "1 - patch.bat" first.
    goto :end
)

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
set "LIST="
for %%F in ("patched\*.apk") do set LIST=!LIST! "%%F"

echo  Installing over the copy on the phone. The data file is large; this takes a few minutes ...
"%ADB%" -s !DEV! install-multiple -r !LIST!
if not errorlevel 1 goto :installed

echo.
echo  That did not work. The usual reason is that the copy on the phone was signed with a
echo  different key ^(the original game, or a copy patched before the key was saved^).
echo  It then has to be removed first. REMOVING IT ERASES THE GAME'S DOWNLOADED FILES
echo  ON THAT PHONE. Only continue if those files are already copied to a PC.
echo.
set "OK="
set /p "OK=  Type YES to remove the copy on the phone and install again: "
if /i not "!OK!"=="YES" (
    echo  Nothing was changed.
    goto :end
)
echo.
"%ADB%" -s !DEV! uninstall com.Everguild.WarhammerWarpforge
echo.
echo  Installing ...
"%ADB%" -s !DEV! install-multiple !LIST!
if errorlevel 1 (
    echo.
    echo  The install did not succeed. The message above says why.
    goto :end
)
:installed
echo.
echo  Installed. Start the game on the phone. The first start can take anywhere
echo  from a minute to 20 or more minutes while the loader sets itself up - leave it open.
echo  If it asks for "All files access", allow it.
:end
echo.
pause
