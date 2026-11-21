@echo off
rem FolderDeck: build (Debug, whole solution), run the Core tests, then start the app; stops at the first failing step.
rem Usage: build-test-run.bat [app arguments as for run.bat, e.g. --data-dir E:\tmp\data]
rem Logic and messages live in tools\dev.ps1 (this file stays ASCII so any code page reads it).
setlocal
set "FD_CMDLINE=%cmdcmdline%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev.ps1" all %*
exit /b %ERRORLEVEL%
