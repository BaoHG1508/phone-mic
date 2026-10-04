@echo off
rem PhoneMic PC receiver launcher for Windows.
rem First run creates a private Python environment and installs the two dependencies.
rem Any arguments are passed through, e.g.:  run_receiver.bat --list-devices
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo Creating Python environment ^(first run only^)...
    py -3 -m venv .venv 2>nul || python -m venv .venv 2>nul || goto :nopython
    ".venv\Scripts\python.exe" -m pip install --quiet --upgrade pip
    ".venv\Scripts\python.exe" -m pip install --quiet -r requirements.txt || goto :pipfail
)

".venv\Scripts\python.exe" phonemic_receiver.py %*
echo.
pause
exit /b 0

:nopython
echo.
echo Python 3.9 or newer was not found.
echo Install it from https://www.python.org/downloads/ and tick "Add python.exe to PATH".
pause
exit /b 1

:pipfail
echo.
echo Installing dependencies failed. Check your internet connection and run this file again.
pause
exit /b 1
