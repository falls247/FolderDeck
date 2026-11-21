@echo off
rem FolderDeck: build (Debug) and start the app.  Usage: run.bat [app arguments, e.g. --data-dir E:\tmp\data]
rem Logic and messages live in tools\dev.ps1 (this file stays ASCII so any code page reads it).
setlocal
set "FD_CMDLINE=%cmdcmdline%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev.ps1" run %*
exit /b %ERRORLEVEL%
