@echo off
setlocal
rem Installs the signed builds from signed\ on this computer: the PC mod into the game, and the Android mod into
rem "android patch\mods" (then put it on the phone with "3 - put mod on phone.bat" there, as before).
rem Do this before or right after pushing to the server, so your own game has the same file the server hands out.
rem The builds are WFRevival.dll / WFRevival.Android.dll (the rename, 0.15.0). The PC mod goes to Mods\WFRevival and any
rem old-named copy is removed, so only one loads.
cd /d "%~dp0"
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40k Warpforge"
set "PCNEW=%GAME%\Mods\WFRevival"
set "PCOLD=%GAME%\Mods\WarpforgeRevival"
set "ANDROID=%~dp0..\android patch\mods"

echo.
echo Checking the signatures...
powershell -NoProfile -ExecutionPolicy Bypass -File "sign-mod.ps1" -Check
if errorlevel 1 (
  echo Not installed: the files in signed\ are missing or not signed with your key.
  echo.
  pause
  exit /b 1
)
echo.

rem (No paths inside ( ) blocks: the game's folder has "(x86)" in it, and its ")" would end the block early -
rem that is what skipped the Android copy before.)
if not exist "signed\WFRevival.dll" goto android
:check
tasklist /FI "IMAGENAME eq Warpforge.exe" 2>nul | find /I "Warpforge.exe" >nul
if errorlevel 1 goto pcinstall
echo The game is running. Close it, then press a key to carry on.
pause >nul
goto check

:pcinstall
rem ---- new place; the old copies are removed so only one loads
if not exist "%PCNEW%" mkdir "%PCNEW%"
if not exist "%PCNEW%\manifest.json" call :manifest
copy /y "signed\WFRevival.dll" "%PCNEW%\WFRevival.dll" >nul
if errorlevel 1 goto pcfailed
if exist "%PCOLD%\WarpforgeRevival.dll" del /q "%PCOLD%\WarpforgeRevival.dll"
if exist "%PCOLD%\manifest.json" del /q "%PCOLD%\manifest.json"
if exist "%PCOLD%" rmdir "%PCOLD%" 2>nul
if exist "%GAME%\Mods\WarpforgeRevival.dll" del /q "%GAME%\Mods\WarpforgeRevival.dll"
echo PC mod installed in "%PCNEW%"
goto android

:pcfailed
echo COULD NOT COPY the PC mod - try Run as administrator.

:android
if not exist "%ANDROID%" mkdir "%ANDROID%"
if not exist "signed\WFRevival.Android.dll" goto done
copy /y "signed\WFRevival.Android.dll" "%ANDROID%\WFRevival.Android.dll" >nul
if errorlevel 1 goto androidfailed
rem the phone bat puts whichever is there on the phone: only the new one stays
if exist "%ANDROID%\WarpforgeRevival.Android.dll" del /q "%ANDROID%\WarpforgeRevival.Android.dll"
echo Android mod copied to "%ANDROID%" (WFRevival.Android.dll)
goto done
:androidfailed
echo COULD NOT COPY the Android mod to "%ANDROID%"

:done
echo.
pause
exit /b 0

:manifest
rem MelonLoader only loads mods from a folder inside Mods when that folder holds a manifest.json
> "%PCNEW%\manifest.json" echo {
>> "%PCNEW%\manifest.json" echo   "name": "WFRevival",
>> "%PCNEW%\manifest.json" echo   "description": "WF Revival mod. MelonLoader needs this file to load mods from this folder - do not delete it."
>> "%PCNEW%\manifest.json" echo }
exit /b 0
