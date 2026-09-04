@echo off
rem Uninstall DesktopFPS: stop it, remove autostart shortcut, delete the exe.
taskkill /f /im DesktopFPS.exe >nul 2>&1
del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\DesktopFPS.lnk" >nul 2>&1
del /f /q "%~dp0DesktopFPS.exe" >nul 2>&1
echo DesktopFPS removed.
pause
