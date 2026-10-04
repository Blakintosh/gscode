@echo off
setlocal
rem Every Category=Corpus test: the diagnostic sweep plus the harvests, the resolution and
rem record-format checks, and the rest of the suites that read real game scripts.
rem
rem   corpus.bat             cod4 and bo3
rem   corpus.bat all         every game set on this machine, two to three minutes
rem   corpus.bat waw         exactly these
rem
rem A corpus run REWRITES server\tests\GSCode.Server.Tests\harvest\*.json and the
rem Api\<game>_stock_scripts.txt files from what it swept. They are listed at the end: a diff
rem there after an unrelated change is the committed file being stale, not the change.

call "%~dp0_env.bat"
call "%~dp0_corpus.bat" %*
if errorlevel 1 (
    set "RESULT=1"
    goto :end
)

dotnet test "%TESTS%\GSCode.Server.Tests\GSCode.Server.Tests.csproj" -c %CONFIG% --nologo --filter "Category=Corpus" --logger "console;verbosity=detailed"
set "RESULT=%ERRORLEVEL%"

echo.
echo Files the run rewrote, if any:
git -C "%ROOT%" status --short -- "server/tests/GSCode.Server.Tests/harvest" "server/src/GSCode.Workspace/Api/*_stock_scripts.txt"
echo Reports: %ROOT%\temp\

:end
call "%~dp0_pause.bat"
exit /b %RESULT%
