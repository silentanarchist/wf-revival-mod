@echo off
setlocal
cd /d "%~dp0"
title Warpforge for Android - fix the loading video

echo.
echo  Warpforge for Android - the opening video for phones that have trouble with it
echo  ------------------------------------------------------------------------------
echo.
echo  Two things for phones that have trouble with the opening video:
echo   - a WebM copy of the clip, for phones whose H.264 decoder plays it blocky, discoloured
echo     or stuttering (seen on the Pixel 6 Pro); the mod plays that copy instead.
echo   - the clip's last frame as a picture, for the mod's "Still" loading-video setting (set
echo     with "7 - loading video setting.bat"): no video is decoded at all - on the Pixel 6 Pro
echo     decoding it leaves the menu black about 1 start in 5 - and the loading screen shows
echo     that picture instead.
echo  Both are made from your own game file with ffmpeg and copied to the phone. The game
echo  itself is not changed; option 2 removes them again.
echo.
echo    1 = make the video and the picture and put them on the phone
echo    2 = remove them from the phone again
echo.
choice /c 12 /n /m "  Press 1 or 2: "
set "CH=1"
if errorlevel 2 set "CH=2"

set "ADB="
if exist "tool\platform-tools\adb.exe" set "ADB=%~dp0tool\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"
if not defined ADB (
    echo  This needs Google's "platform-tools" ^(the adb program^) in tool\platform-tools.
    goto :end
)

set "WORK=work-temp\video"
rem v2: keyframes where the game seeks when the video is skipped (see the ffmpeg line)
set "WEBM=%WORK%\intro-v2.webm"
set "STILL=%WORK%\intro-last.rgba"
if "%CH%"=="2" goto :phone

rem ---- make the WebM (kept in work-temp, so a second phone does not need it made again)
if exist "%WEBM%" if exist "%STILL%" (
    echo  Using the video and picture made earlier in %WORK%
    goto :phone
)
set "APK=game-files\com.Everguild.WarhammerWarpforge.apk"
if not exist "%APK%" (
    echo  %APK% is missing. Run "1 - patch.bat" first; it puts the game's files there.
    goto :end
)
set "FF="
if exist "tool\ffmpeg\ffmpeg.exe" set "FF=%~dp0tool\ffmpeg\ffmpeg.exe"
if not defined FF if exist "tool\ffmpeg\bin\ffmpeg.exe" set "FF=%~dp0tool\ffmpeg\bin\ffmpeg.exe"
if not defined FF for /f "delims=" %%A in ('where ffmpeg 2^>nul') do if not defined FF set "FF=%%A"
if not defined FF (
    echo  This needs ffmpeg ^(a free video converter^), which is not included here.
    echo  Either:
    echo    - download "ffmpeg-release-essentials.zip" from https://www.gyan.dev/ffmpeg/builds/
    echo      and copy ffmpeg.exe from its "bin" folder into tool\ffmpeg\ in this folder, or
    echo    - run   winget install Gyan.FFmpeg   in a command prompt, then open a new window.
    echo  Then run this again.
    goto :end
)
if not exist "%WORK%" mkdir "%WORK%"
echo  Copying the video out of the game file ...
powershell -NoProfile -ExecutionPolicy Bypass -File "tool\extract-intro.ps1" -Apk "%APK%" -Out "%WORK%\intro-original.mp4"
if errorlevel 1 (
    echo  Could not copy the video out of the game file.
    goto :end
)
if exist "%WEBM%" goto :still
echo  Converting it to WebM. This takes a minute or two ...
rem Tapping skips the video by jumping to 0.1 s before its end and letting it play out to the last
rem frame. A WebM can only jump to a keyframe, so: a keyframe every 2 s like the original, and every
rem frame from 9.4 s on (the video is 9.72 s long) is one, so the jump lands right before the end.
"%FF%" -hide_banner -loglevel error -y -i "%WORK%\intro-original.mp4" -c:v libvpx -b:v 3M -deadline good -cpu-used 1 -auto-alt-ref 1 -g 50 -force_key_frames "expr:gte(t,n_forced*2)+gte(t,9.4)" -c:a libvorbis -q:a 4 "%WORK%\intro-new.webm"
if errorlevel 1 (
    echo  ffmpeg could not convert the video ^(the lines above say why^).
    del "%WORK%\intro-new.webm" 2>nul
    goto :end
)
move /y "%WORK%\intro-new.webm" "%WEBM%" >nul
for %%S in ("%WEBM%") do echo  Made %WEBM% ^(%%~zS bytes^).
:still
rem The clip's last frame, 1440x810, raw 32-bit RGBA, bottom row first (Unity's texture layout): 4,665,600 bytes.
echo  Taking the clip's last frame as a picture ...
"%FF%" -hide_banner -loglevel error -y -sseof -0.2 -i "%WORK%\intro-original.mp4" -update 1 -frames:v 1 -vf "scale=1440:810,vflip" -f rawvideo -pix_fmt rgba "%STILL%"
if errorlevel 1 (
    echo  ffmpeg could not take the picture ^(the lines above say why^).
    del "%STILL%" 2>nul
    goto :end
)
del "%WORK%\intro-original.mp4" 2>nul
for %%S in ("%STILL%") do echo  Made %STILL% ^(%%~zS bytes^).

:phone
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
    if "%CH%"=="1" echo  The video is made already; run this again once the phone is connected.
    goto :end
)
for /f "delims=" %%M in ('""%ADB%" -s !DEV! shell getprop ro.product.model"') do set "MODEL=%%M"
echo  Using device: !DEV! ^(!MODEL!^)

set "C=/sdcard/MelonLoader/com.Everguild.WarhammerWarpforge/UserData/WarpforgeRevival/content"
if "%CH%"=="2" (
    "%ADB%" -s !DEV! shell rm -f !C!/intro.webm !C!/intro-last.rgba
    echo  Removed. The game plays its original video again from the next start ^(unless the
    echo  loading-video setting is Still or Off; "7 - loading video setting.bat" sets it back^).
    goto :end
)

"%ADB%" -s !DEV! shell mkdir -p !C!
"%ADB%" -s !DEV! push "%WEBM%" !C!/intro.webm
if errorlevel 1 (
    echo  Could not copy the video to the phone ^(the line above says why^).
    goto :end
)
"%ADB%" -s !DEV! push "%STILL%" !C!/intro-last.rgba
if errorlevel 1 (
    echo  Could not copy the video to the phone ^(the line above says why^).
    goto :end
)
for %%S in ("%WEBM%") do set "WANT=%%~zS"
set "GOT="
for /f "delims=" %%G in ('""%ADB%" -s !DEV! shell stat -c %%s !C!/intro.webm"') do set "GOT=%%G"
if not "!GOT!"=="!WANT!" (
    echo  The copy on the phone is not complete ^(!GOT! of !WANT! bytes^). Run this again.
    goto :end
)
echo.
echo  Done. Close the game fully and start it again. With the loading-video setting on
echo  Normal the opening video now plays from the WebM copy; on Still the picture is shown
echo  instead of any video. To go back to the original, run this again and choose 2.

:end
echo.
pause
