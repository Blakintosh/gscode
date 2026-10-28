@echo off
setlocal
rem Builds a scale workspace to open in the editor, without measuring it. Same generator and the same
rem variables as scale.bat, so the folder it makes is the one a scale run measures.
rem
rem   scale-workspace.bat                  bo3, 10,000 files, scripts\zm\_zm.gsc duplicated
rem   scale-workspace.bat 50000            the same at 50,000
rem   set GSCODE_SCALE_SOURCE=             the mixed stock-tree shape instead
rem   set GSCODE_SCALE_NAMESPACE=same      every copy left in one namespace (pathological)
rem
rem It prints the .code-workspace path. Open it with "Run Extension (scale workspace)" in
rem .vscode\launch.json, giving the file's name without the extension (bo3-10000-zm), or with
rem File > Open Workspace from File in an editor that has the extension installed.

set "GSCODE_SCALE_SIZES="
:args
if "%~1"=="" goto :argsdone
if defined GSCODE_SCALE_SIZES (set "GSCODE_SCALE_SIZES=%GSCODE_SCALE_SIZES%,%~1") else (set "GSCODE_SCALE_SIZES=%~1")
shift
goto :args
:argsdone
if not defined GSCODE_SCALE_SIZES set "GSCODE_SCALE_SIZES=10000"
if not defined GSCODE_SCALE_GAMES set "GSCODE_SCALE_GAMES=bo3"
if not defined GSCODE_SCALE_SOURCE set "GSCODE_SCALE_SOURCE=heaviest"

call "%~dp0_env.bat"
call "%~dp0_corpus.bat" %GSCODE_SCALE_GAMES:,= %
if errorlevel 1 (
    set "RESULT=1"
    goto :end
)

dotnet test "%TESTS%\GSCode.Server.Tests\GSCode.Server.Tests.csproj" -c %CONFIG% --nologo --filter "Category=ScaleWorkspace" --logger "console;verbosity=detailed"
set "RESULT=%ERRORLEVEL%"

:end
call "%~dp0_pause.bat"
exit /b %RESULT%
