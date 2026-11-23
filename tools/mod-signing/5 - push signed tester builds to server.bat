@echo off
setlocal
rem Publishes the signed TESTER builds in signed-test\ to the server (W:\mod-test). Only testers' and admins' games
rem get them (mod 0.15.32 and later ask for both builds and take the newer). Only builds signed with your key are copied.
rem The next main build (pushed with "3 - push signed mods to server.bat") is newer than every tester build of the one
rem before, so testers move to it; build its tester builds (x.y.z.1, .2, ...) from there.
rem From 0.15.0 the builds are WFRevival.dll / WFRevival.Android.dll (the rename): when one of those is in signed-test\,
rem it is pushed and the old-named file of that platform is removed from the server (the server serves either name,
rem the new one first). The download address players' games use has no file name in it, so 0.14.x games update too.
cd /d "%~dp0"
set "SERVER=W:\mod-test"

echo.
if not exist "W:\mod\" (
  echo W:\mod is not reachable - is the W: drive connected?
  echo.
  pause
  exit /b 1
)
if not exist "%SERVER%\" mkdir "%SERVER%"
echo Checking the signatures...
powershell -NoProfile -ExecutionPolicy Bypass -File "sign-mod.ps1" -Check -Tester
if errorlevel 1 (
  echo Nothing pushed: the files in signed-test\ are missing or not signed with your key.
  echo.
  pause
  exit /b 1
)
echo.
echo This sends the tester builds above to the server; testers get them at their next game start.
choice /C YN /M "Push them now"
if errorlevel 2 (
  echo Nothing pushed.
  echo.
  pause
  exit /b 0
)
call :one WFRevival.dll WarpforgeRevival.dll
call :one WFRevival.Android.dll WarpforgeRevival.Android.dll
echo.
pause
exit /b 0

rem :one <new name> <old name> - push the new-named build if there is one (and drop the old-named file from the
rem server), otherwise the old-named build
:one
if exist "signed-test\%~1" goto onenew
if not exist "signed-test\%~2" exit /b 0
copy /y "signed-test\%~2" "%SERVER%\%~2.new" >nul && move /y "%SERVER%\%~2.new" "%SERVER%\%~2" >nul
if errorlevel 1 (echo COULD NOT PUSH %~2) else (echo Pushed %~2)
exit /b 0
:onenew
copy /y "signed-test\%~1" "%SERVER%\%~1.new" >nul && move /y "%SERVER%\%~1.new" "%SERVER%\%~1" >nul
if errorlevel 1 (
  echo COULD NOT PUSH %~1
  exit /b 0
)
echo Pushed %~1
if exist "%SERVER%\%~2" del /q "%SERVER%\%~2" && echo Removed the old-named %~2 from the server
exit /b 0
