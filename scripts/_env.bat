@echo off
rem Shared setup for the scripts beside it. CALLed by them, never run on its own: it has no
rem setlocal, so what it sets lands in the caller's environment.
rem
rem   ROOT, SERVER, CLIENT, TESTS   the repository and its parts
rem   CONFIG                        Debug or Release, see below

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "SERVER=%ROOT%\server"
set "CLIENT=%ROOT%\client"
set "TESTS=%ROOT%\server\tests"

rem Build the configuration the editor's language server is NOT running from. A running server
rem holds its DLLs open, and building over them fails with MSB3027 partway through. F5 uses
rem Debug and the Release launch config uses Release, so look rather than assume.
rem
rem   RELEASE_LOCKED   1 when a GSCode server is running from bin\Release
rem   CONFIG           GSCODE_CONFIG if set, else Debug when Release is locked, else Release
set "RELEASE_LOCKED=0"
for /f "usebackq delims=" %%L in (`powershell -NoProfile -Command "@(Get-CimInstance Win32_Process -Filter 'Name=''dotnet.exe''' | Where-Object { $_.CommandLine -match 'GSCode\.Server\.dll' -and $_.CommandLine -match 'bin.Release' }).Count"`) do (
    if not "%%L"=="0" set "RELEASE_LOCKED=1"
)

if defined GSCODE_CONFIG (
    set "CONFIG=%GSCODE_CONFIG%"
) else if "%RELEASE_LOCKED%"=="1" (
    set "CONFIG=Debug"
    echo A GSCode server is running from bin\Release, so this builds Debug.
) else (
    set "CONFIG=Release"
)
goto :eof
