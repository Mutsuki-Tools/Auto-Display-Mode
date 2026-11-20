@echo off
setlocal

rem Build AutoDisplayMode.exe with the C# compiler built into Windows (.NET Framework 4.x)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto NOCSC

"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:"%~dp0AutoDisplayMode.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%~dp0AutoDisplayMode.cs"
if errorlevel 1 goto FAIL

echo.
echo Build OK: %~dp0AutoDisplayMode.exe
pause
exit /b 0

:NOCSC
echo csc.exe not found. .NET Framework 4.x is required.
pause
exit /b 1

:FAIL
echo.
echo Build FAILED. See the errors above.
pause
exit /b 1