@echo off
setlocal
rem Builds the extension package, client\gscode-<version>.vsix, and stops there: nothing is
rem published. Install it with
rem
rem   code --install-extension client\gscode-<version>.vsix
rem
rem Packaging publishes the server in Release into client\service, from the working tree as it
rem is, uncommitted changes included. That cannot happen while an editor runs the Release server
rem from bin\Release, so this checks first rather than failing ten retries into the build.

rem Packaging is always Release; _env.bat is here for the lock check.
set "GSCODE_CONFIG=Release"
call "%~dp0_env.bat"
if "%RELEASE_LOCKED%"=="1" (
    echo A GSCode server is running from bin\Release. Close the editor running it first:
    echo packaging has to rebuild it.
    set "RESULT=1"
    goto :end
)

pushd "%CLIENT%"

if not exist node_modules (
    call npm ci
    if errorlevel 1 (
        set "RESULT=1"
        goto :done
    )
)

rem Type-checks, then the server publish, then esbuild's bundle into out\ through vsce.
call npm run package
set "RESULT=%ERRORLEVEL%"

rem The package leaves out\ holding only the bundle; F5 runs tsc's output, so put that back.
call npm run compile >nul

if "%RESULT%"=="0" (
    echo.
    rem Newest first, and only that one: older packages stay in the folder.
    for /f "delims=" %%F in ('dir /b /o-d gscode-*.vsix') do (
        echo Built %CLIENT%\%%F
        goto :done
    )
)

:done
popd

:end
call "%~dp0_pause.bat"
exit /b %RESULT%
