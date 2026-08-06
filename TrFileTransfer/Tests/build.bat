@echo off
cd /d "%~dp0"
set FRAMEWORK=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FRAMEWORK%\csc.exe

if not exist "%CSC%" (
    set FRAMEWORK=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319
    set CSC=%FRAMEWORK%\csc.exe
)
if not exist "%CSC%" (
    echo Error: C# compiler not found. Install .NET Framework 4.x SDK.
    exit /b 1
)

echo Building TrFileTransfer Tests...
echo.

"%CSC%" ^
    /nologo ^
    /target:exe ^
    /out:TrFileTransfer.Tests.exe ^
    /reference:"%FRAMEWORK%\System.dll" ^
    /reference:"%FRAMEWORK%\System.Core.dll" ^
    /optimize+ ^
    ..\Config.cs ^
    ..\Shared.cs ^
    ..\L10N.cs ^
    ..\ConcurrentTransfer.cs ^
    ..\TransferServer.cs ^
    ..\TransferUdt.cs ^
    ..\TransferClient.cs ^
    ..\DeviceDiscovery.cs ^
    ..\ResumeState.cs ^
    ..\ServerResumeStore.cs ^
    TestProgram.cs ^
    IntegrationTests.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo ====================================================
    echo   Build successful: TrFileTransfer.Tests.exe
    echo ====================================================
) else (
    echo.
    echo Build FAILED.
    exit /b %ERRORLEVEL%
)
