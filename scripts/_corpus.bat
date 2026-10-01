@echo off
rem Picks which game corpora a run reads. CALLed by corpus.bat, sweep.bat and perf.bat with
rem their arguments:
rem
rem   (nothing)          cod4 and bo3, the two dialect families
rem   all                every game whose GSCODE_CORPUS_<GAME> is set on this machine
rem   cod4 waw mw2 ...   exactly these
rem
rem A corpus test whose variable is empty no-ops and PASSES, so a run that read nothing looks
rem exactly like success. This clears the variables of the games not asked for, lists the ones
rem that will be read, and fails when that list is empty.

set "GAMES= %* "
if "%~1"=="" set "GAMES= cod4 bo3 "
if /i "%~1"=="all" set "GAMES= cod4 waw mw2 bo1 bo3 "

set "CORPUS_COUNT=0"
echo Corpora:
for %%Q in (COD4 WAW MW2 BO1 BO3) do (
    echo %GAMES% | findstr /i /c:" %%Q " >nul
    if errorlevel 1 (
        set "GSCODE_CORPUS_%%Q="
    ) else if defined GSCODE_CORPUS_%%Q (
        set /a CORPUS_COUNT+=1
        call echo   %%Q  %%GSCODE_CORPUS_%%Q%%
    ) else (
        echo   %%Q  NOT SET - GSCODE_CORPUS_%%Q is empty, so this game is skipped
    )
)
echo.

if "%CORPUS_COUNT%"=="0" (
    echo No corpus to read. Set GSCODE_CORPUS_COD4, _WAW, _MW2, _BO1 or _BO3 to a game's raw
    echo folder, then open a new terminal: variables set at user scope do not reach one already open.
    exit /b 1
)
exit /b 0
