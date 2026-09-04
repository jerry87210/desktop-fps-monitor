@echo off
rem Rebuild DesktopFPS.exe using the .NET Framework compiler bundled with Windows.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo .NET Framework compiler not found.
    pause
    exit /b 1
)
taskkill /f /im DesktopFPS.exe >nul 2>&1
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0DesktopFPS.exe" "%~dp0DesktopFPS.cs" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo Build OK: "%~dp0DesktopFPS.exe"
pause
