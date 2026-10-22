@echo off
rem Double-click to build the finished Playnest.exe into publish\Playnest\.
rem It builds ONE self-contained exe (the same kind as the GitHub releases): copy it anywhere and run it.
rem For the NativeAOT build (a folder: exe + DLLs) run:  powershell -File publish.ps1 -Native
setlocal
cd /d "%~dp0"
set AVALONIA_TELEMETRY_OPTOUT=1

echo Closing Playnest if it is running...
taskkill /im Playnest.exe /f >nul 2>&1
dotnet build-server shutdown >nul 2>&1

echo.
echo === Building Playnest.exe (this can take a few minutes the first time) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if %errorlevel%==0 goto done

echo.
echo BUILD FAILED. Scroll up for the first error, or send it to Claude.
pause
exit /b 1

:done
echo.
echo Finished. Opening the folder with Playnest.exe...
explorer "%~dp0publish\Playnest"
pause
