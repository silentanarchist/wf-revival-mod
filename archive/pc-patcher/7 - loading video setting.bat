@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - loading video setting

echo.
echo  Warpforge for Android - set how the loading screen's video is played (phone test)
echo  ---------------------------------------------------------------------------------
echo.
echo  Changes the LoadingVideo setting in the mod's settings file on the phone
echo  (UserData\WarpforgeRevival.cfg). Takes effect on the next start of the game.
echo.
echo    1 = Still    no video is decoded; the clip's last frame is shown as a picture instead
echo                 (needs "6 - fix loading video.bat" run once). The fix for phones whose
echo                 video decoder leaves the menu black, like the Pixel 6 Pro.
echo    2 = Normal   the game's own player (the usual setting)
echo    3 = Off      no video, dark loading screen (test only)
echo    4 = Texture  the video drawn through a texture (test only; did not help)
echo.
choice /c 1234 /n /m "  Press 1, 2, 3 or 4: "
set "MODE=Still"
if errorlevel 2 set "MODE=Normal"
if errorlevel 3 set "MODE=Off"
if errorlevel 4 set "MODE=Texture"

set "ADB="
if exist "tool\platform-tools\adb.exe" set "ADB=%~dp0tool\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"
if not defined ADB (
    echo  This needs Google's "platform-tools" ^(the adb program^) in tool\platform-tools.
    goto :end
)
echo.
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
for /f "delims=" %%M in ('""%ADB%" -s !DEV! shell getprop ro.product.model"') do set "MODEL=%%M"
echo  Using device: !DEV! ^(!MODEL!^)

set "CFG=/sdcard/MelonLoader/com.Everguild.WarhammerWarpforge/UserData/WarpforgeRevival.cfg"
if not exist "work-temp" mkdir "work-temp"
"%ADB%" -s !DEV! pull "!CFG!" "work-temp\WarpforgeRevival.cfg" >nul 2>&1
if not exist "work-temp\WarpforgeRevival.cfg" (
    echo  The mod's settings file is not on the phone yet. Start the game once with the mod, then run this again.
    goto :end
)
powershell -NoProfile -Command "$p='work-temp\WarpforgeRevival.cfg'; $t=[IO.File]::ReadAllText($p); if ($t -match '(?m)^\s*LoadingVideo\s*=') { $t=[regex]::Replace($t,'(?m)^(\s*LoadingVideo\s*=\s*).*$','${1}\"%MODE%\"') } else { $t=$t.TrimEnd()+\"`r`nLoadingVideo = `\"%MODE%`\"`r`n\" }; [IO.File]::WriteAllText($p,$t)"
"%ADB%" -s !DEV! push "work-temp\WarpforgeRevival.cfg" "!CFG!" >nul
if errorlevel 1 (
    echo  Could not copy the settings file back to the phone.
    goto :end
)
for /f "delims=" %%L in ('""%ADB%" -s !DEV! shell grep LoadingVideo !CFG!"') do set "NOW=%%L"
echo.
echo  Done: !NOW!
echo  Close the game fully and start it again for this to take effect.

:end
echo.
pause
