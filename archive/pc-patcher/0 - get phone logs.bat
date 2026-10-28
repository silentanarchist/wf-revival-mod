@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - collect the phone's logs

echo.
echo  Warpforge for Android - collect the phone's logs
echo  ------------------------------------------------
echo.
set "ADB="
if exist "tool\platform-tools\adb.exe" set "ADB=%~dp0tool\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"
if not defined ADB (
    echo  This needs Google's "platform-tools" ^(the adb program^).
    echo  Download "SDK Platform-Tools for Windows" from
    echo    https://developer.android.com/tools/releases/platform-tools
    echo  and unzip it so that this file exists:
    echo    %~dp0tool\platform-tools\adb.exe
    goto :end
)
echo  Phone connected by USB with "USB debugging" on? Devices found:
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
echo  Which is it?
echo     1 = the game is closed and I am about to start it ^(a fresh start^)
echo     2 = the game is open and something just went wrong - save the logs as they are now
choice /c 12 /n /m "  Press 1 or 2: "
if errorlevel 2 goto :collect
echo.
echo  Clearing the phone's old system log so the new one is not crowded out ...
"%ADB%" -s !DEV! logcat -G 16M >nul 2>&1
"%ADB%" -s !DEV! logcat -c >nul 2>&1
echo.
echo  IMPORTANT: if the game is already open, close it first ^(swipe it away^).
echo  The game has to be started AFTER this point for the log to be complete.
echo.
echo  Now start the game on the phone, then WAIT. The first start after an install
echo  copies a lot of files and can sit on a black screen for a few minutes.
echo  Come back and press a key only when one of these has happened:
echo     - the game closed by itself, or
echo     - the game reached its menu, or
echo     - the game froze or three minutes have passed with nothing changing.
echo  Press the key soon after - within a minute or so.
pause >nul

:collect

set "P=/sdcard/MelonLoader/com.Everguild.WarhammerWarpforge"
rem a new folder for every run, so nothing old is ever mistaken for new
set "STAMP=run"
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%T"
set "OUT=phone-logs\!STAMP!"
if not exist "!OUT!" mkdir "!OUT!"
echo  Saving the phone's system log ...
"%ADB%" -s !DEV! logcat -d -v threadtime > "!OUT!\logcat.txt" 2>&1
echo  Saving the loader's own logs ...
"%ADB%" -s !DEV! shell "cat %P%/MelonLoader/Latest.log" > "!OUT!\Latest.log" 2>&1
"%ADB%" -s !DEV! shell "ls -la %P% %P%/MelonLoader %P%/MelonLoader/net6 %P%/MelonLoader/Il2CppAssemblies %P%/Mods %P%/UserData" > "!OUT!\folder-listing.txt" 2>&1
"%ADB%" -s !DEV! shell "dumpsys package com.Everguild.WarhammerWarpforge | grep -E 'versionName|firstInstallTime|lastUpdateTime|MANAGE_EXTERNAL'; appops get com.Everguild.WarhammerWarpforge MANAGE_EXTERNAL_STORAGE" > "!OUT!\app-state.txt" 2>&1
echo  Copying the loader's log folder ^(every start-up it has recorded^) ...
"%ADB%" -s !DEV! pull "%P%/MelonLoader/Logs" "!OUT!\Logs" > "!OUT!\pull-result.txt" 2>&1
"%ADB%" -s !DEV! pull "%P%/UserData" "!OUT!\UserData" >> "!OUT!\pull-result.txt" 2>&1
rem the saved sign-in works like a password: it is not kept with the logs
del "!OUT!\UserData\WarpforgeRevival\game-login.txt" 2>nul
"%ADB%" -s !DEV! shell "ls -laR /sdcard/Android/data/com.Everguild.WarhammerWarpforge/files 2>&1 | head -200" > "!OUT!\game-data-listing.txt" 2>&1
echo  Saving the phone's own reports of the app freezing or crashing ^(this can take a minute^) ...
"%ADB%" -s !DEV! shell "dumpsys dropbox --print data_app_anr 2>&1 | tail -c 3000000" > "!OUT!\freeze-reports.txt" 2>&1
"%ADB%" -s !DEV! shell "dumpsys dropbox --print data_app_native_crash 2>&1 | tail -c 1000000" > "!OUT!\crash-reports.txt" 2>&1
echo.
echo  Done. Everything is in the "!OUT!" folder.
:end
echo.
pause
