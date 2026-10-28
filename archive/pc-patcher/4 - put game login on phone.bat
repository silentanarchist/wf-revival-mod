@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - put your game login on the phone

echo.
echo  Warpforge for Android - put your game login on the phone
echo  ----------------------------------------------------------
echo.
echo  The game login is the file you download on the Revival website (Profile page,
echo  "Download game login file"). With it on the phone, the game signs in as your
echo  account. Treat it like a password.
echo.

rem ---- find the file: dropped on this .bat, next to it, or the newest one in Downloads ----
set "LOGIN="
if not "%~1"=="" if exist "%~1" set "LOGIN=%~f1"
if not defined LOGIN if exist "game-login.txt" set "LOGIN=%~dp0game-login.txt"
if not defined LOGIN for /f "delims=" %%F in ('dir /b /o-d "%USERPROFILE%\Downloads\game-login*.txt" 2^>nul') do if not defined LOGIN set "LOGIN=%USERPROFILE%\Downloads\%%F"
if not defined LOGIN (
    echo  No game login file found. Download it from the website, then either drop it onto
    echo  this .bat file, put it next to this .bat file, or leave it in your Downloads folder.
    goto :end
)
findstr /b /c:"wfr-login-" "%LOGIN%" >nul
if errorlevel 1 (
    echo  "%LOGIN%"
    echo  is not a game login file ^(it should start with wfr-login-^). Nothing was copied.
    goto :end
)
echo  Using: %LOGIN%
echo.

set "ADB="
if exist "tool\platform-tools\adb.exe" set "ADB=%~dp0tool\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"
if not defined ADB (
    echo  This needs Google's "platform-tools" ^(the adb program^) in tool\platform-tools.
    echo  See "3 - put mod on phone.bat" or READ ME.txt.
    goto :end
)

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
echo  Using device: !DEV!
set "P=/sdcard/MelonLoader/com.Everguild.WarhammerWarpforge/UserData/WarpforgeRevival"
"%ADB%" -s !DEV! shell mkdir -p %P%
"%ADB%" -s !DEV! push "%LOGIN%" %P%/game-login.txt >nul
if errorlevel 1 (
    echo.
    echo  Copying failed. Has the patched game been started at least once on this phone?
    goto :end
)
echo.
echo  Done: the game login is on the phone. Close the game completely and start it again;
echo  it will sign in as your account.
echo.
echo  The file is still on this PC ^(%LOGIN%^). Delete it if nobody else should be able
echo  to use it - the phone has its own copy now.

:end
echo.
pause
