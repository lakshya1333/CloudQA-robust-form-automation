@echo off
rem Builds the project and runs it against the bundled variant pages and the live CloudQA page.
rem Uses a .NET 8 SDK from PATH if present, otherwise installs one portably into D:\dotnet.
rem Output is written to build-and-test-log.txt next to this file.
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET=dotnet
echo ===== dotnet sdk ===== > build-and-test-log.txt
dotnet --list-sdks 2>nul | findstr /b "8. 9. 10." >nul
if errorlevel 1 (
  if not exist "D:\dotnet\dotnet.exe" (
    echo Installing .NET 8 SDK into D:\dotnet ... >> build-and-test-log.txt
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol='Tls12'; Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1; & $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir D:\dotnet -NoPath" >> build-and-test-log.txt 2>&1
  )
  set DOTNET=D:\dotnet\dotnet.exe
  set DOTNET_ROOT=D:\dotnet
)
"%DOTNET%" --list-sdks >> build-and-test-log.txt 2>&1
echo ===== build ===== >> build-and-test-log.txt
"%DOTNET%" build -c Release >> build-and-test-log.txt 2>&1
echo BUILD_EXIT=%ERRORLEVEL% >> build-and-test-log.txt
echo ===== suite (TestPages) ===== >> build-and-test-log.txt
"%DOTNET%" run -c Release --no-build -- --suite TestPages --headless >> build-and-test-log.txt 2>&1
echo SUITE_EXIT=%ERRORLEVEL% >> build-and-test-log.txt
echo ===== live page ===== >> build-and-test-log.txt
"%DOTNET%" run -c Release --no-build -- --headless >> build-and-test-log.txt 2>&1
echo LIVE_EXIT=%ERRORLEVEL% >> build-and-test-log.txt
echo ===== DONE ===== >> build-and-test-log.txt
