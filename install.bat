@echo off
setlocal
rem OnionMedia-Batch installer: run this file from the unpacked release folder (next to Install.ps1).
rem - removes the "downloaded from the internet" mark from the release files,
rem - runs Install.ps1 (installs the certificate + the app) without changing the system execution policy,
rem - closes by itself when the installation succeeded; stays open with a message if it failed.
cd /d "%~dp0"
if not exist "%~dp0Install.ps1" (
    echo Install.ps1 was not found next to install.bat.
    echo Unpack the whole release archive first and run install.bat from that folder.
    pause
    exit /b 1
)
PowerShell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File | Unblock-File -ErrorAction SilentlyContinue; & '%~dp0Install.ps1'; exit $LASTEXITCODE"
if errorlevel 1 (
    echo.
    echo OnionMedia was NOT installed ^(error code %errorlevel%^). Read the message above, then press any key to close.
    pause >nul
    exit /b %errorlevel%
)
echo OnionMedia was installed successfully. You can find it in the Start Menu.
timeout /t 3 >nul
exit /b 0
