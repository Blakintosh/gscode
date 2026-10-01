@echo off
setlocal
rem The perf sweep: per-file timing over the stock scripts, split into lex, preprocess, parse and
rem extract, plus the handler costs. A second full pass over every script, so slower than the
rem diagnostic sweep.
rem
rem   perf.bat               cod4 and bo3
rem   perf.bat all           every game set on this machine
rem   perf.bat bo3           exactly these
rem
rem Always Release unless GSCODE_CONFIG says otherwise: Debug timings measure the JIT's
rem unoptimised code, not GSCode. If an editor is running the Release server, close it first.
rem The HTML reports are written to temp\ (GSCODE_PERF_REPORT overrides the folder).

if not defined GSCODE_CONFIG set "GSCODE_CONFIG=Release"
call "%~dp0_env.bat"
if "%RELEASE_LOCKED%"=="1" if /i "%CONFIG%"=="Release" (
    echo A GSCode server is running from bin\Release and holds its DLLs. Close the editor running
    echo it, or set GSCODE_CONFIG=Debug to accept Debug timings.
    set "RESULT=1"
    goto :end
)
call "%~dp0_corpus.bat" %*
if errorlevel 1 (
    set "RESULT=1"
    goto :end
)

dotnet test "%TESTS%\GSCode.Server.Tests\GSCode.Server.Tests.csproj" -c %CONFIG% --nologo --filter "Category=Perf" --logger "console;verbosity=detailed"
set "RESULT=%ERRORLEVEL%"

echo.
echo Reports: %ROOT%\temp\gscode-perf-*.html

:end
call "%~dp0_pause.bat"
exit /b %RESULT%
