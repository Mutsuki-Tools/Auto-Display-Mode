@echo off
setlocal

rem ============================================================
rem  Monitor mode switcher (requires NirSoft MultiMonitorTool)
rem  All monitor settings are listed below and applied with
rem  /SetMonitors by fixed Short Monitor ID, so it works no matter
rem  how Windows numbers DISPLAY1/2/3.
rem
rem  Usage:
rem    monitormode.bat            show menu
rem    monitormode.bat home       switch to Home mode
rem    monitormode.bat away       switch to Away mode
rem ============================================================

rem ---- Monitor IDs (see 'Short Monitor ID' column in MultiMonitorTool) ----
set "MSI=MSI3DD8"
set "ACER=ACR0833"
set "WACOM=WAC1090"

rem ---- Home mode: all 3 monitors ----
rem  DisplayOrientation: 0=landscape 1=portrait 2=landscape(flipped) 3=portrait(flipped)
rem  For portrait, Width/Height are swapped (e.g. 1440 x 2560).
set "HOME_MSI=Name=%MSI% Primary=1 BitsPerPixel=32 Width=3840 Height=2160 DisplayFrequency=160 DisplayOrientation=0 PositionX=0 PositionY=0"
set "HOME_ACER=Name=%ACER% BitsPerPixel=32 Width=1440 Height=2560 DisplayFrequency=170 DisplayOrientation=0 PositionX=-2560 PositionY=0"
set "HOME_WACOM=Name=%WACOM% BitsPerPixel=32 Width=2560 Height=1600 DisplayFrequency=60 DisplayOrientation=0 PositionX=-2560 PositionY=1440"

rem ---- Away mode: MSI only ----
set "AWAY_MSI=Name=%MSI% Primary=1 BitsPerPixel=32 Width=2560 Height=1440 DisplayFrequency=60 DisplayOrientation=0 PositionX=0 PositionY=0"
set "AWAY_OFF=%ACER% %WACOM%"

rem ---- Display numbers to turn on first (disabled monitors have no readable ID) ----
set "ENABLE_NUMS=1 2 3 4"

set "MMT=%~dp0MultiMonitorTool.exe"
set "ARG=%~1"

if not exist "%MMT%" (
  echo MultiMonitorTool.exe not found. Put it in the same folder as this script.
  pause
  exit /b 1
)

if /i "%ARG%"=="home" goto HOME
if /i "%ARG%"=="away" goto AWAY

:MENU
echo.
echo  [1] Home mode  - 3 monitors
echo  [2] Away mode  - MSI only, 2560x1440 @ 60Hz
echo  [3] Open MultiMonitorTool
echo  [0] Exit
echo.
choice /c 1230 /n /m "Select: "
if errorlevel 4 exit /b 0
if errorlevel 3 goto LIST
if errorlevel 2 goto AWAY
if errorlevel 1 goto HOME

:HOME
rem 1. Turn on every monitor so their IDs can be read
"%MMT%" /enable %ENABLE_NUMS%
call :WAIT 3
rem 2. Apply the layout to all 3 monitors by ID
"%MMT%" /SetMonitors "%HOME_MSI%" "%HOME_ACER%" "%HOME_WACOM%"
call :WAIT 2
rem 3. Make sure MSI is primary
"%MMT%" /SetPrimary %MSI%
echo Switched to Home mode.
goto END

:AWAY
rem 1. Turn on every monitor so their IDs can be read (no change if already on)
"%MMT%" /enable %ENABLE_NUMS%
call :WAIT 3
rem 2. MSI becomes primary, then turn the others off
"%MMT%" /SetPrimary %MSI%
call :WAIT 1
"%MMT%" /disable %AWAY_OFF%
call :WAIT 2
rem 3. Set MSI resolution
"%MMT%" /SetMonitors "%AWAY_MSI%"
echo Switched to Away mode.
goto END

:LIST
start "" "%MMT%"
goto END

:WAIT
rem Wait about %1 seconds
ping -n %~1 127.0.0.1 >nul
ping -n 2 127.0.0.1 >nul
exit /b 0

:END
if "%ARG%"=="" pause
exit /b 0
