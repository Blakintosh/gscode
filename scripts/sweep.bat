@echo off
setlocal
rem The diagnostic sweep: every lint over the stock scripts, grouped by code. The stock scripts
rem shipped and work, so anything reported is a real defect or, far more often, a false
rem positive. Run it before shipping any diagnostic change.
rem
rem   sweep.bat              cod4 and bo3, about thirty seconds
rem   sweep.bat all          every game set on this machine, a few minutes
rem   sweep.bat mw2 bo1      exactly these
rem
rem The report is written to temp\ (GSCODE_SWEEP_REPORT overrides the folder).

call "%~dp0_env.bat"
call "%~dp0_corpus.bat" %*
if errorlevel 1 (
    set "RESULT=1"
    goto :end
)

dotnet test "%TESTS%\GSCode.Server.Tests\GSCode.Server.Tests.csproj" -c %CONFIG% --nologo --filter "FullyQualifiedName~CorpusDiagnosticSweepTests" --logger "console;verbosity=detailed"
set "RESULT=%ERRORLEVEL%"

echo.
echo Reports: %ROOT%\temp\

:end
call "%~dp0_pause.bat"
exit /b %RESULT%
