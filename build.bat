@echo off
echo ============================================
echo  TaskbarMusic build ^& run
echo ============================================
echo.

REM Compute ONE timestamp here and pin it as BUILD_ID for BOTH restore and build.
REM If we leave BUILD_ID empty, Directory.Build.props falls back to DateTime.Now
REM which is evaluated separately by restore and build -> two different obj folders
REM -> build cannot find project.assets.json written by restore. Pinning it fixes that,
REM while still using a brand-new folder each run to avoid the locked-cache issue.
for /f "delims=" %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMddHHmmss"') do set "BUILD_ID=%%i"
echo BUILD_ID=%BUILD_ID%

REM Kill running instance, otherwise exe is locked and cannot be overwritten
echo Killing running instance...
taskkill /F /IM TaskbarMusic.exe >nul 2>&1
dotnet build-server shutdown >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo.
echo [1/2] Building Debug...
dotnet build -c Debug --nologo
if errorlevel 1 (
    echo.
    echo *** BUILD FAILED ***
    pause
    exit /b 1
)

REM EXE path is fully deterministic from BUILD_ID. Build straight from it,
REM no PowerShell scan (which returned empty in the real shell before).
echo.
echo Locating exe...
set "EXE=%LOCALAPPDATA%\TaskbarMusic\bin\%BUILD_ID%\Debug\net9.0-windows10.0.19041.0\TaskbarMusic.exe"

echo.
echo ============================================
echo  Build succeeded!
echo ============================================
echo EXE: %EXE%
if not exist "%EXE%" (
    echo *** EXE NOT FOUND ***
    pause
    exit /b 1
)
for %%f in ("%EXE%") do echo Built at: %%~tf
echo.

echo [2/2] Launching...
start "" "%EXE%"
echo Done. Check "Built at" above matches current time.
pause
