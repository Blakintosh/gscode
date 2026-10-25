@echo off
setlocal
rem The scale suite: generated workspaces of thousands of stock-script copies, measuring cold and
rem warm start, memory and every request through its handler, against budgets.
rem
rem   scale.bat                    10,000 files
rem   scale.bat 10000 50000        each size in turn
rem
rem The copies are made from the BO3 corpus (GSCODE_SCALE_GAMES picks others) and go under
rem %TEMP%\gscode-scale, or GSCODE_SCALE_ROOT. They are GIGABYTES at 50,000, kept between runs so
rem the next one does not rebuild them, and never cleaned up: delete the folder when done.
rem Release, for the same reason as perf.bat.
rem
rem GSCODE_SCALE_SOURCE=heaviest duplicates ONE script instead (bo3: scripts\zm\_zm.gsc, ~2,950
rem calls) so every stock function it calls is referenced once per copy, and writes its own report,
rem temp\gscode-scale-<stem>.md. heaviest:N copies the N largest round-robin; PERF.md's 50,000-file
rem numbers use heaviest:25 on bo3 and cod4 (GSCODE_SCALE_GAMES=bo3,cod4).
rem GSCODE_SCALE_NAMESPACE=same leaves every copy in one namespace.
rem The heavy shape retains several times what the mixed one does: ramp the sizes (2000 5000 10000)
rem and read "KB a file" before asking for 50,000, and set a heap limit so a size that does not fit
rem fails with OutOfMemory rather than paging the machine: DOTNET_GCHeapHardLimit=0x3C0000000 (15 GB)
rem is what the recorded 50,000-file runs used. Every scale variable is in server\tests\FOLDER.md.

set "GSCODE_SCALE_SIZES="
:args
if "%~1"=="" goto :argsdone
if defined GSCODE_SCALE_SIZES (set "GSCODE_SCALE_SIZES=%GSCODE_SCALE_SIZES%,%~1") else (set "GSCODE_SCALE_SIZES=%~1")
shift
goto :args
:argsdone
if not defined GSCODE_SCALE_SIZES set "GSCODE_SCALE_SIZES=10000"
if not defined GSCODE_SCALE_GAMES set "GSCODE_SCALE_GAMES=bo3"

if not defined GSCODE_CONFIG set "GSCODE_CONFIG=Release"
call "%~dp0_env.bat"
if "%RELEASE_LOCKED%"=="1" if /i "%CONFIG%"=="Release" (
    echo A GSCode server is running from bin\Release and holds its DLLs. Close the editor running
    echo it, or set GSCODE_CONFIG=Debug to accept Debug timings.
    set "RESULT=1"
    goto :end
)
call "%~dp0_corpus.bat" %GSCODE_SCALE_GAMES:,= %
if errorlevel 1 (
    set "RESULT=1"
    goto :end
)

echo Sizes: %GSCODE_SCALE_SIZES%
dotnet test "%TESTS%\GSCode.Server.Tests\GSCode.Server.Tests.csproj" -c %CONFIG% --nologo --filter "Category=Scale" --logger "console;verbosity=detailed"
set "RESULT=%ERRORLEVEL%"

echo.
echo Report: %ROOT%\temp\gscode-scale.md

:end
call "%~dp0_pause.bat"
exit /b %RESULT%
