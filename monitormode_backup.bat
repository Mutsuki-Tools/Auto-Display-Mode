@echo off
setlocal

rem ============================================================
rem  Monitor mode switcher (requires NirSoft MultiMonitorTool)
rem  Put MultiMonitorTool.exe in the same folder as this script.
rem
rem  Usage:
rem    monitor_mode.bat            show menu
rem    monitor_mode.bat home       switch to Home mode
rem    monitor_mode.bat away       switch to Away mode
rem    monitor_mode.bat savehome   save current layout as Home mode
rem    monitor_mode.bat saveaway   save current layout as Away mode
rem ============================================================

set "MMT=%~dp0MultiMonitorTool.exe"
set "HOME_CFG=%~dp0home.cfg"
set "AWAY_CFG=%~dp0away.cfg"
set "ARG=%~1"

if not exist "%MMT%" (
  echo MultiMonitorTool.exe not found. Put it in the same folder as this script.
  pause
  exit /b 1
)

if /i "%ARG%"=="home" goto HOME
if /i "%ARG%"=="away" goto AWAY
if /i "%ARG%"=="savehome" goto SAVEHOME
if /i "%ARG%"=="saveaway" goto SAVEAWAY

:MENU
echo.
echo  [1] Home mode  - 3 monitors
echo  [2] Away mode  - 4K only, at 2K resolution
echo  [3] Save current layout as Home mode
echo  [4] Save current layout as Away mode
echo  [5] Open MultiMonitorTool
echo  [0] Exit
echo.
choice /c 123450 /n /m "Select: "
if errorlevel 6 exit /b 0
if errorlevel 5 goto LIST
if errorlevel 4 goto SAVEAWAY
if errorlevel 3 goto SAVEHOME
if errorlevel 2 goto AWAY
if errorlevel 1 goto HOME

:HOME
if not exist "%HOME_CFG%" (
  echo Home mode not saved yet. Set up all 3 monitors, then use option 3.
  goto END
)
"%MMT%" /LoadConfig "%HOME_CFG%"
echo Switched to Home mode.
goto END

:AWAY
if not exist "%AWAY_CFG%" (
  echo Away mode not saved yet. Set up the single monitor, then use option 4.
  goto END
)
"%MMT%" /LoadConfig "%AWAY_CFG%"
echo Switched to Away mode.
goto END

:SAVEHOME
"%MMT%" /SaveConfig "%HOME_CFG%"
echo Saved current layout as Home mode: %HOME_CFG%
goto END

:SAVEAWAY
"%MMT%" /SaveConfig "%AWAY_CFG%"
echo Saved current layout as Away mode: %AWAY_CFG%
goto END

:LIST
start "" "%MMT%"
goto END

:END
if "%ARG%"=="" pause
exit /b 0