@echo off
setlocal EnableExtensions
title VoiceGuard 6.5.5 - Build Installer

cd /d "%~dp0"

echo ============================================================
echo   VoiceGuard 6.5.5 - One-Click Installer Build
echo   Jack The Gooner
echo   CPU-only Whisper runtime / x64
echo ============================================================
echo.

REM ---- Check .NET SDK ----
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found.
    echo.
    echo Install the .NET 8 SDK, then run this file again.
    echo.
    pause
    exit /b 1
)

echo [1/6] Checking .NET SDK...
dotnet --version
echo.

REM ---- Check Inno Setup ----
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
    echo [ERROR] Inno Setup 6 was not found.
    echo.
    echo Install Inno Setup 6, then run this file again.
    echo.
    pause
    exit /b 1
)

echo [2/6] Inno Setup found:
echo        %ISCC%
echo.

REM ---- Clean old output ----
echo [3/6] Cleaning previous publish and installer output...
if exist "publish" rmdir /s /q "publish"
if exist "installer" rmdir /s /q "installer"
mkdir "publish"
mkdir "installer"
echo.

REM ---- Publish self-contained x64 multi-file app ----
echo [4/6] Publishing VoiceGuard with CPU-only Whisper runtime...
echo Native Whisper runtime is intentionally kept beside VoiceGuard.exe.
dotnet publish "VoiceGuard.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  /p:PublishSingleFile=false ^
  /p:PublishTrimmed=false ^
  /p:DebugType=None ^
  /p:DebugSymbols=false ^
  -o "publish"

if errorlevel 1 (
    echo.
    echo [ERROR] VoiceGuard publish failed.
    echo.
    pause
    exit /b 1
)

if not exist "publish\VoiceGuard.exe" (
    echo.
    echo [ERROR] Publish completed but VoiceGuard.exe was not created.
    echo.
    pause
    exit /b 1
)

for %%A in ("publish\VoiceGuard.exe") do set "EXESIZE=%%~zA"
echo.
echo VoiceGuard.exe created successfully.
echo.
echo WHISPER NATIVE RUNTIME CHECK
REM Whisper.net.Runtime intentionally publishes native libraries under
REM publish\runtimes\win-x64\ rather than beside VoiceGuard.exe.
if not exist "publish\runtimes\win-x64\whisper.dll" (
    echo [ERROR] publish\runtimes\win-x64\whisper.dll was not found.
    echo The Whisper CPU native runtime was not published correctly.
    echo.
    pause
    exit /b 1
)
if not exist "publish\runtimes\win-x64\ggml-whisper.dll" (
    echo [ERROR] publish\runtimes\win-x64\ggml-whisper.dll was not found.
    echo The Whisper native dependency was not published correctly.
    echo.
    pause
    exit /b 1
)
if not exist "publish\runtimes\win-x64\ggml-base-whisper.dll" (
    echo [ERROR] publish\runtimes\win-x64\ggml-base-whisper.dll was not found.
    echo The Whisper native dependency was not published correctly.
    echo.
    pause
    exit /b 1
)
if not exist "publish\runtimes\win-x64\ggml-cpu-whisper.dll" (
    echo [ERROR] publish\runtimes\win-x64\ggml-cpu-whisper.dll was not found.
    echo The Whisper native dependency was not published correctly.
    echo.
    pause
    exit /b 1
)
echo Whisper native runtime found at:
echo publish\runtimes\win-x64\
echo VoiceGuard.exe size: %EXESIZE% bytes
echo.

REM ---- Reject accidentally huge builds ----
set /a "MAXSIZE=500000000"
if %EXESIZE% GTR %MAXSIZE% (
    echo [ERROR] VoiceGuard.exe is larger than 500 MB.
    echo.
    pause
    exit /b 1
)

REM ---- Compile installer ----
echo [5/6] Compiling Windows installer...
"%ISCC%" "VoiceGuard_Installer.iss"

if errorlevel 1 (
    echo.
    echo [ERROR] Inno Setup failed to compile the installer.
    echo.
    pause
    exit /b 1
)

if not exist "installer\VoiceGuard_Setup_6.5.5.exe" (
    echo.
    echo [ERROR] Inno Setup reported success, but the installer was not found.
    echo.
    pause
    exit /b 1
)

for %%A in ("installer\VoiceGuard_Setup_6.5.5.exe") do set "SETUPSIZE=%%~zA"

echo.
echo [6/6] Final verification...
echo Installer size: %SETUPSIZE% bytes
echo.

echo ============================================================
echo   BUILD SUCCESSFUL
echo ============================================================
echo.
echo VoiceGuard EXE:
echo %CD%\publish\VoiceGuard.exe
echo.
echo Installer:
echo %CD%\installer\VoiceGuard_Setup_6.5.5.exe
echo.
pause
exit /b 0
