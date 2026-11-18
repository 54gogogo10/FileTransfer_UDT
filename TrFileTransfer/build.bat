@echo off
setlocal
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

rem ---- Version comes from AssemblyInfo.cs (single source of truth) ----
set "RAW="
for /f "tokens=2 delims=()" %%v in ('findstr /c:"AssemblyVersion" AssemblyInfo.cs') do (
    if not defined RAW set "RAW=%%v"
)
set "VER=%RAW:"=%"
if not defined VER (
    echo Warning: could not read AssemblyVersion from AssemblyInfo.cs - using 0.0.0.0
    set "VER=0.0.0.0"
)

rem ---- Versioned, directly distributable copy under release\ ----
set "OUT=release"
set "BASE=TrFileTransfer-%VER%"
if not exist "%OUT%" mkdir "%OUT%"
copy /Y "bin\Release\net48\TrFileTransfer.exe" "%OUT%\%BASE%.exe" >nul
copy /Y "bin\Release\net48\TrFileTransfer.exe.config" "%OUT%\%BASE%.exe.config" >nul 2>&1

rem ---- sha256 sidecar, same convention as .github/workflows/release.yml ----
rem Get-FileHash lives in a PowerShell module that is not always importable (a stripped
rem PSModulePath gives "The term 'Get-FileHash' is not recognized"). The .NET fallback
rem keeps the sidecar correct everywhere, and a short/invalid hash fails the build
rem instead of silently writing a file that only contains the exe name.
powershell -NoProfile -Command "$p='release\%BASE%.exe'; if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) { $h=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower() } else { $s=[IO.File]::OpenRead($p); try { $a=[Security.Cryptography.SHA256]::Create(); $h=([BitConverter]::ToString($a.ComputeHash($s)) -replace '-','').ToLower() } finally { $s.Dispose() } }; if ($h.Length -ne 64) { Write-Error 'sha256 computation failed'; exit 1 }; ($h + '  %BASE%.exe') | Out-File -Encoding ascii -NoNewline ('release\%BASE%.exe.sha256')"
if errorlevel 1 (
    echo Error: failed to write the SHA256 sidecar.
    exit /b 1
)

rem ---- Unversioned copy kept for .github/workflows/{ci,release}.yml, which read this path ----
copy /Y "bin\Release\net48\TrFileTransfer.exe" "TrFileTransfer.exe" >nul
copy /Y "bin\Release\net48\TrFileTransfer.exe.config" "TrFileTransfer.exe.config" >nul 2>&1

echo.
echo ========================================
echo   Build successful  (version %VER%)
echo     %OUT%\%BASE%.exe
if exist "%OUT%\%BASE%.exe.sha256" echo     %OUT%\%BASE%.exe.sha256
echo     TrFileTransfer.exe   [CI compatibility copy]
echo ========================================
endlocal
