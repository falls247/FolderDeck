@echo off
rem FolderDeck: make all three single-file exes and copy them under dist\
rem   dist\FolderDeck.exe (self-contained, compressed), dist\uncompressed\FolderDeck.exe (self-contained),
rem   dist\framework-dependent\FolderDeck.exe (needs the .NET 10 desktop runtime). Stops at the first failure.
rem Logic and messages live in tools\dev.ps1 (this file stays ASCII so any code page reads it).
setlocal
set "FD_CMDLINE=%cmdcmdline%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev.ps1" publish all
exit /b %ERRORLEVEL%
