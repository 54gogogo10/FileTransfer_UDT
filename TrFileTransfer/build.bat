@echo off
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo Error: dotnet SDK not found. Install .NET SDK 8+ from https://dotnet.microsoft.com
    exit /b 1
)

echo Building TrFileTransfer (net48, WPF)...
dotnet build -c Release -v minimal --nologo
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b %errorlevel%
)

echo.
echo ========================================
copy /Y "bin\Release\net48\TrFileTransfer.exe" "TrFileTransfer.exe" >nul
copy /Y "bin\Release\net48\TrFileTransfer.exe.config" "TrFileTransfer.exe.config" >nul 2>&1
echo   Build successful: TrFileTransfer.exe
echo ========================================
