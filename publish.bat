@echo off
rem FolderDeck: make the single-file exe and copy it to dist\FolderDeck.exe
rem Logic and messages live in tools\dev.ps1 (this file stays ASCII so any code page reads it).
setlocal
set "FD_CMDLINE=%cmdcmdline%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev.ps1" publish %*
exit /b %ERRORLEVEL%
