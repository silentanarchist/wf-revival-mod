@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - uninstall from the phone

echo.
echo  Warpforge for Android - remove the game, the loader and the mod from a phone
echo  ---------------------------------------------------------------------------
echo.

set "ADB="
if exist "tool\platform-tools\adb.exe" set "ADB=%~dp0tool\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"
if not defined ADB (
    echo  This needs Google's "platform-tools" ^(the adb program^) in tool\platform-tools.
    echo  Or uninstall from the phone itself: hold the game's icon, Uninstall, then delete
    echo  the folder MelonLoader\com.Everguild.WarhammerWarpforge with a file manager.
    goto :end
)

echo  Phone connected by USB, with "USB debugging" switched on?
echo  Devices found:
"%ADB%" devices
echo.

setlocal enabledelayedexpansion
set "DEV="
for /f "skip=1 tokens=1,2" %%A in ('""%ADB%" devices"') do (
    set "N=%%A"
    if "%%B"=="device" if /i not "!N:~0,9!"=="emulator-" if not defined DEV set "DEV=%%A"
)
if not defined DEV for /f "skip=1 tokens=1,2" %%A in ('""%ADB%" devices"') do if "%%B"=="device" if not defined DEV set "DEV=%%A"
if not defined DEV (
    echo  No phone is ready. Check the cable, that USB debugging is on, and that the
    echo  "Allow USB debugging?" prompt on the phone was accepted.
    goto :end
)
for /f "delims=" %%M in ('""%ADB%" -s %DEV% shell getprop ro.product.model"') do set "MODEL=%%M"
echo  Using device: !DEV! ^(!MODEL!^)
echo.
echo  This removes from that phone:
echo    - the game ^(Warhammer 40,000: Warpforge^) and everything it downloaded
echo    - the mod loader, the mod, its settings and its logs
echo      ^(MelonLoader\com.Everguild.WarhammerWarpforge^)
echo    - the saved game sign-in, so this phone is signed out
echo.
echo  Your account, decks and progress are on the server and are NOT affected.
echo  The phone's settings file and logs are copied to this computer first
echo  ^(folder "uninstall-backup"^), without the saved sign-in.
echo.
set "OK="
set /p "OK=  Type YES to remove it from !MODEL!: "
if /i not "!OK!"=="YES" (
    echo  Nothing was changed.
    goto :end
)

set "P=/sdcard/MelonLoader/com.Everguild.WarhammerWarpforge"
for /f "delims=" %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%T"
set "B=uninstall-backup\!STAMP!"
mkdir "!B!" 2>nul
echo.
echo  Copying the settings file and logs to !B! ...
"%ADB%" -s !DEV! pull %P%/UserData/WarpforgeRevival.cfg "!B!\WarpforgeRevival.cfg" >nul 2>&1
"%ADB%" -s !DEV! pull %P%/MelonLoader/Latest.log "!B!\Latest.log" >nul 2>&1
"%ADB%" -s !DEV! pull %P%/MelonLoader/Logs "!B!\Logs" >nul 2>&1

echo  Removing the game ...
"%ADB%" -s !DEV! uninstall com.Everguild.WarhammerWarpforge
echo  Removing the loader and mod files ...
"%ADB%" -s !DEV! shell rm -rf %P%
rem the MelonLoader folder itself goes too, but only when nothing else is in it
"%ADB%" -s !DEV! shell "rmdir /sdcard/MelonLoader 2>/dev/null"

echo.
"%ADB%" -s %DEV% shell "pm list packages com.Everguild.WarhammerWarpforge" | find "com.Everguild" >nul
if not errorlevel 1 (
    echo  The game is still installed - the message above says why.
    goto :end
)
"%ADB%" -s %DEV% shell "[ -d %P% ] && echo LEFT" | find "LEFT" >nul
if not errorlevel 1 (
    echo  The game is gone, but some loader files could not be removed:
    echo    %P%
    echo  Delete that folder on the phone with a file manager.
    goto :end
)
echo  Done. Warpforge, the loader and the mod are gone from !MODEL!.
echo  To play on this phone again, start from "2 - install on phone.bat".

:end
echo.
pause
