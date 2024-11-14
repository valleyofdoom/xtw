@echo off
pushd %~dp0

xtw.exe --delay 3 --timed 10

if not %errorlevel%  == 0 (
    pause
    exit /b 1
)

start notepad xtw-report.txt

pause
exit /b 0