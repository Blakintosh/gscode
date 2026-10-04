@echo off
setlocal
rem The everyday unit tests: the three test projects with the filter CI uses. No game installs
rem needed, a minute or two.
rem
rem Per project rather than the solution: a running language server locks its DLLs, and a
rem solution build fails partway through with no clear culprit.

call "%~dp0_env.bat"

set "RESULT=0"
for %%P in (Parser Workspace Server) do (
    echo.
    echo === GSCode.%%P.Tests, %CONFIG%
    dotnet test "%TESTS%\GSCode.%%P.Tests\GSCode.%%P.Tests.csproj" -c %CONFIG% --nologo --filter "Category!=Corpus&Category!=Perf"
    if errorlevel 1 set "RESULT=1"
)

echo.
if "%RESULT%"=="0" (echo All three test projects passed.) else (echo FAILED - scroll up for the project that failed.)

call "%~dp0_pause.bat"
exit /b %RESULT%
