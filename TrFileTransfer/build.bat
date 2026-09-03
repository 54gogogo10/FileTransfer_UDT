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

echo Building TrFileTransfer...

set RESOURCE=
if exist "udt.dll" (
    echo   Embedding udt.dll...
    set RESOURCE=/resource:udt.dll,TrFileTransfer.udt.dll
) else (
    echo   WARNING: udt.dll not found. UDT transfers will fail at runtime until udt.dll is placed alongside the exe.
)
set RESOURCE_MCF=
if exist "libmcfgthread-2.dll" (
    echo   Embedding libmcfgthread-2.dll...
    set RESOURCE_MCF=/resource:libmcfgthread-2.dll,TrFileTransfer.libmcfgthread-2.dll
) else (
    echo   WARNING: libmcfgthread-2.dll not found. UDT may fail due to missing thread runtime.
)

echo.

"%CSC%" ^
    /nologo ^
    /target:winexe ^
    /out:TrFileTransfer.exe ^
    /reference:"%FRAMEWORK%\System.dll" ^
    /reference:"%FRAMEWORK%\System.Core.dll" ^
    /reference:"%FRAMEWORK%\System.Windows.Forms.dll" ^
    /reference:"%FRAMEWORK%\System.Drawing.dll" ^
    /optimize+ ^
    /doc:TrFileTransfer.xml ^
    %RESOURCE% ^
    %RESOURCE_MCF% ^
    Config.cs ^
    Shared.cs ^
    L10N.cs ^
    WireProtocol.cs ^
    TransferServer.cs ^
    ResumeState.cs ^
    ServerResumeStore.cs ^
    FolderResumeState.cs ^
    ConcurrentTransfer.cs ^
    TransferUdt.cs ^
    TransferClient.cs ^
    DeviceDiscovery.cs ^
    MainForm.cs ^
    Program.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================
    echo   Build successful: TrFileTransfer.exe
    echo ========================================
) else (
    echo.
    echo Build FAILED.
    exit /b %ERRORLEVEL%
)
