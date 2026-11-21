@echo off
rem FolderDeck: run the Core unit tests.  Usage: test.bat [extra dotnet test arguments]
rem Logic and messages live in tools\dev.ps1 (this file stays ASCII so any code page reads it).
setlocal
set "FD_CMDLINE=%cmdcmdline%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev.ps1" test %*
exit /b %ERRORLEVEL%
