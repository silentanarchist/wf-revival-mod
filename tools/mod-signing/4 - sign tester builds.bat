@echo off
rem Signs the testers' builds in to-sign-test\ with your key; the signed copies go to signed-test\.
rem A tester build is the main version plus a fourth number (0.15.32.1, .2, ...): changes for testers only.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "sign-mod.ps1" -Tester
echo.
pause
