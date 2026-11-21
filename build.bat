@echo off
rem FolderDeck: build the solution.  Usage: build.bat [Debug or Release]
rem Logic and messages live in tools\dev.ps1 (this file stays ASCII so any code page reads it).
setlocal
set "FD_CMDLINE=%cmdcmdline%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev.ps1" build %*
exit /b %ERRORLEVEL%
