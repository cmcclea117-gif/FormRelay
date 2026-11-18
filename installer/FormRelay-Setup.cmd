@echo off
setlocal
title FormRelay setup

rem Double-click launcher for FormRelay-Setup.ps1. It asks Windows for administrator rights, runs the script
rem without the PowerShell execution-policy block, and clears the "downloaded from the internet" mark.
rem To skip the menu, pass a mode:   FormRelay-Setup.cmd update -Latest

net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo   FormRelay setup needs administrator permission, because it installs a Windows service,
  echo   opens a firewall port for your local network, and creates a database login.
  echo   Before it changes anything it will show you exactly what it will do and ask you first.
  echo.
  echo   This file is plain text, so you can read it. It also lets the setup script in this folder
  echo   run, because Windows blocks downloaded scripts by default. Source: github.com/cmcclea117-gif/FormRelay
  rem Note: never put a closing parenthesis in an echo line inside one of these blocks, it ends the block early.
  echo.
  choice /c YN /n /m "Continue and ask Windows for permission? [Y/N] "
  if errorlevel 2 exit /b
  echo Asking Windows for administrator permission...
  rem Start-Process rejects an empty -ArgumentList, so only pass one when there are arguments.
  if "%~1"=="" (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -WorkingDirectory '%~dp0'"
  ) else (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs -WorkingDirectory '%~dp0'"
  )
  if errorlevel 1 (
    echo.
    echo Could not get administrator permission. Right-click this file and choose "Run as administrator".
    pause
  )
  exit /b
)

cd /d "%~dp0"
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File | Unblock-File" >nul 2>&1

if not "%~1"=="" goto passthrough

:menu
echo.
echo   FormRelay setup
echo   ---------------
echo   1. Install FormRelay on this computer
echo   2. Update an existing install to the latest release
echo   3. Check an existing install (changes nothing)
echo   4. Quit
echo.
choice /c 1234 /n /m "Choose 1-4: "
if errorlevel 4 exit /b
if errorlevel 3 (set "SETUPARGS=check" & goto run)
if errorlevel 2 (set "SETUPARGS=update -Latest" & goto run)
if errorlevel 1 (set "SETUPARGS=install" & goto run)
goto menu

:passthrough
set "SETUPARGS=%*"

:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0FormRelay-Setup.ps1" %SETUPARGS%
set "RC=%errorlevel%"
echo.
echo Finished (exit code %RC%). Press any key to close this window.
pause >nul
exit /b %RC%
