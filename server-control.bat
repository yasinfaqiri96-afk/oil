@echo off
setlocal EnableExtensions

cd /d "%~dp0"
set "PTG_PROJECT_ROOT=%~dp0"

if /I "%~1"=="restart" goto restart
if /I "%~1"=="stop" goto stop
if not "%~1"=="" goto usage

:menu
echo.
echo ========================================
echo   PTG Oil System - Server Control
echo ========================================
echo   [R] Restart project server
echo   [S] Stop project server
echo   [Q] Quit
echo.
choice /C RSQ /N /M "Select an option: "
if errorlevel 3 goto end
if errorlevel 2 goto stop
if errorlevel 1 goto restart

:restart
call :stop_server
if errorlevel 1 goto failed

if not exist "%~dp0run-system.bat" (
    echo ERROR: run-system.bat was not found.
    goto failed
)

echo Starting PTG Oil System in a new window...
start "PTG Oil System" "%ComSpec%" /c call "%~dp0run-system.bat"
if errorlevel 1 goto failed

call :wait_for_server
if errorlevel 1 goto failed

echo Restart command completed.
goto success

:stop
call :stop_server
if errorlevel 1 goto failed
goto success

:stop_server
echo Checking for PTG Oil System server processes...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$root = [IO.Path]::GetFullPath($env:PTG_PROJECT_ROOT).TrimEnd([IO.Path]::DirectorySeparatorChar);" ^
    "$project = Join-Path $root 'src\PTGOilSystem.Web\PTGOilSystem.Web.csproj';" ^
    "$dll = Join-Path $root 'src\PTGOilSystem.Web\bin\Debug\net8.0\PTGOilSystem.Web.dll';" ^
    "$webDir = Join-Path $root 'src\PTGOilSystem.Web';" ^
    "$listeners = @(Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue);" ^
    "$listenerIds = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique);" ^
    "$processes = @(Get-CimInstance Win32_Process);" ^
    "$targets = @($processes | Where-Object {" ^
    "    $command = [string]$_.CommandLine;" ^
    "    $executable = [string]$_.ExecutablePath;" ^
    "    $isProjectCommand = $command.IndexOf($project, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $command.IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -ge 0;" ^
    "    $isProjectExecutable = -not [string]::IsNullOrWhiteSpace($executable) -and $executable.StartsWith($webDir, [StringComparison]::OrdinalIgnoreCase);" ^
    "    $ownsProjectPort = $listenerIds -contains $_.ProcessId;" ^
    "    $isProjectWatch = $_.Name -ieq 'dotnet.exe' -and $isProjectCommand -and $command -match '(?i)\bwatch\b';" ^
    "    ($ownsProjectPort -and ($isProjectCommand -or $isProjectExecutable)) -or $isProjectWatch" ^
    "});" ^
    "if ($targets.Count -eq 0) {" ^
    "    if ($listenerIds.Count -gt 0) { Write-Error 'Port 5000 is used by another application; it was not stopped.'; exit 1 }" ^
    "    else { Write-Host 'No running PTG Oil System server was found.' }" ^
    "    exit 0" ^
    "}" ^
    "$watchTargets = @($targets | Where-Object { ([string]$_.CommandLine) -match '(?i)\bwatch\b' });" ^
    "$serverTargets = @($targets | Where-Object { $watchTargets.ProcessId -notcontains $_.ProcessId });" ^
    "foreach ($processInfo in @($watchTargets + $serverTargets)) {" ^
    "    try { Stop-Process -Id $processInfo.ProcessId -Force -ErrorAction Stop; Write-Host ('Stopped PID {0}: {1}' -f $processInfo.ProcessId, $processInfo.Name) }" ^
    "    catch { if (Get-Process -Id $processInfo.ProcessId -ErrorAction SilentlyContinue) { Write-Error ('Could not stop PID {0}: {1}' -f $processInfo.ProcessId, $_.Exception.Message); exit 1 } }" ^
    "}" ^
    "Start-Sleep -Milliseconds 700;" ^
    "$remaining = @(Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue);" ^
    "if ($remaining.Count -gt 0) { Write-Error 'Port 5000 is still in use. Unknown processes were left untouched.'; exit 1 }" ^
    "Write-Host 'PTG Oil System server stopped successfully.' -ForegroundColor Green"
exit /b %ERRORLEVEL%

:wait_for_server
echo Waiting for PTG Oil System to become ready...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$root = [IO.Path]::GetFullPath($env:PTG_PROJECT_ROOT).TrimEnd([IO.Path]::DirectorySeparatorChar);" ^
    "$dll = Join-Path $root 'src\PTGOilSystem.Web\bin\Debug\net8.0\PTGOilSystem.Web.dll';" ^
    "$deadline = [DateTime]::UtcNow.AddSeconds(25);" ^
    "do {" ^
    "    $listeners = @(Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue);" ^
    "    foreach ($listener in $listeners) {" ^
    "        $owner = Get-CimInstance Win32_Process -Filter ('ProcessId = {0}' -f $listener.OwningProcess) -ErrorAction SilentlyContinue;" ^
    "        if ($owner -and ([string]$owner.CommandLine).IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -ge 0) { Write-Host 'PTG Oil System is ready on http://localhost:5000' -ForegroundColor Green; exit 0 }" ^
    "    }" ^
    "    Start-Sleep -Milliseconds 500" ^
    "} while ([DateTime]::UtcNow -lt $deadline);" ^
    "Write-Error 'PTG Oil System did not become ready on port 5000 within 25 seconds.'; exit 1"
exit /b %ERRORLEVEL%

:usage
echo Usage:
echo   server-control.bat restart
echo   server-control.bat stop
exit /b 2

:failed
echo.
echo Operation failed. Review the message above.
if "%~1"=="" pause
exit /b 1

:success
echo.
if "%~1"=="" pause
exit /b 0

:end
endlocal
exit /b 0
