@echo off
rem Pauses when the calling script was double-clicked, so its window stays open long enough to
rem read the result, and returns at once when it was run from a terminal.
rem
rem Checking %cmdcmdline% for the script's name is the usual trick and is wrong here: PowerShell
rem runs a batch file through `cmd /c` too, so it paused in every PowerShell terminal. This walks
rem up past the cmd.exe processes instead and pauses only when the first thing above them is
rem Explorer.
for /f "usebackq delims=" %%N in (`powershell -NoProfile -Command "$p = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID); do { $p = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $p.ParentProcessId) } while ($p -and $p.Name -eq 'cmd.exe'); if ($p) { $p.Name }"`) do (
    if /i "%%N"=="explorer.exe" pause
)
