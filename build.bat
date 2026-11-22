@echo off
setlocal

rem Build AutoDisplayMode.exe with the .NET SDK, run the tests, and copy the exe and the driver files to release\.
rem The exe targets .NET Framework 4.8 (built into Windows 10/11), so no extra runtime is needed to run it.
rem release\ is the portable program folder: AutoDisplayMode.exe, drivers\sudovda\, README.md (from docs\release\)
rem and AutoDisplayMode.cfg (kept in git; the program creates it on first run). Deleting release\ and building again restores everything but the cfg.

where dotnet >nul 2>nul
if errorlevel 1 goto NODOTNET

set "SLN=%~dp0AutoDisplayMode.sln"
set "OUT=%~dp0src\AutoDisplayMode\bin\Release\net48\AutoDisplayMode.exe"
set "REL=%~dp0release"
set "DRV=%~dp0drivers\sudovda"
set "RELDRV=%REL%\drivers\sudovda"

dotnet build "%SLN%" -c Release --nologo
if errorlevel 1 goto FAIL

dotnet test "%SLN%" -c Release --nologo --no-build
if errorlevel 1 goto TESTFAIL

if not exist "%REL%" mkdir "%REL%"
copy /y "%OUT%" "%REL%\AutoDisplayMode.exe" >nul
if errorlevel 1 goto COPYFAIL

rem Driver package, public certificate and license notice: the program looks for them next to the exe.
rem Start from an empty folder so files removed from drivers\sudovda do not linger in release\.
if exist "%REL%\drivers" rmdir /s /q "%REL%\drivers"
xcopy /y /i /q "%DRV%\x64\*" "%RELDRV%\x64\" >nul
if errorlevel 1 goto DRVFAIL
copy /y "%DRV%\AutoDisplayMode-SudoVDA.cer" "%RELDRV%\" >nul
if errorlevel 1 goto DRVFAIL
copy /y "%DRV%\LICENSE.txt" "%RELDRV%\" >nul
if errorlevel 1 goto DRVFAIL

rem User guide for people who only get the release folder.
copy /y "%~dp0docs\release\README.md" "%REL%\README.md" >nul
if errorlevel 1 goto READMEFAIL

rem Older builds copied the exe to the project root: remove the leftover copy.
if exist "%~dp0AutoDisplayMode.exe" del "%~dp0AutoDisplayMode.exe" >nul 2>nul
if exist "%~dp0AutoDisplayMode.exe" echo Note: could not delete the old %~dp0AutoDisplayMode.exe. Close it from the tray menu and delete it.

echo.
echo Build OK: %REL%\AutoDisplayMode.exe
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
echo Could not copy the exe to %REL%\AutoDisplayMode.exe
echo If the tool is running, close it from the tray menu and build again.
if "%~1"=="" pause
exit /b 1

:DRVFAIL
echo.
echo Could not copy the driver files from %DRV% to %RELDRV%
if "%~1"=="" pause
exit /b 1

:READMEFAIL
echo.
echo Could not copy %~dp0docs\release\README.md to %REL%\README.md
if "%~1"=="" pause
exit /b 1
