@echo off
setlocal

rem Build AutoDisplayMode.exe with the .NET SDK and run the tests.
rem The exe targets .NET Framework 4.8 (built into Windows 10/11), so no extra runtime is needed to run it.

where dotnet >nul 2>nul
if errorlevel 1 goto NODOTNET

set "SLN=%~dp0AutoDisplayMode.sln"
set "OUT=%~dp0src\AutoDisplayMode\bin\Release\net48\AutoDisplayMode.exe"

dotnet build "%SLN%" -c Release --nologo
if errorlevel 1 goto FAIL

dotnet test "%SLN%" -c Release --nologo --no-build
if errorlevel 1 goto TESTFAIL

copy /y "%OUT%" "%~dp0AutoDisplayMode.exe" >nul
if errorlevel 1 goto COPYFAIL

echo.
echo Build OK: %~dp0AutoDisplayMode.exe
if "%~1"=="" pause
exit /b 0

:NODOTNET
echo dotnet not found. Install the .NET SDK: https://dotnet.microsoft.com/download
if "%~1"=="" pause
exit /b 1

:FAIL
echo.
echo Build FAILED. See the errors above.
if "%~1"=="" pause
exit /b 1

:TESTFAIL
echo.
echo Tests FAILED. See the errors above.
if "%~1"=="" pause
exit /b 1

:COPYFAIL
echo.
echo Could not copy the exe to %~dp0AutoDisplayMode.exe
echo If the tool is running, close it from the tray menu and build again.
if "%~1"=="" pause
exit /b 1
