@echo off
pushd %~dp0

xtw.exe --delay 3 --timed 10 --open-report

if not %errorlevel%  == 0 (
    pause
    exit /b 1
)

pause
exit /b 0