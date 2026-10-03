@echo off
rem Double-click to build the finished Playnest.exe into publish\Playnest\.
rem Tries the native build first (smallest, fastest); if the C++ build tools are missing,
rem it falls back to the portable build, which needs nothing extra.
setlocal
cd /d "%~dp0"
set AVALONIA_TELEMETRY_OPTOUT=1

echo Closing Playnest if it is running...
taskkill /im Playnest.exe /f >nul 2>&1
dotnet build-server shutdown >nul 2>&1

echo.
echo === Building native Playnest.exe (this can take a few minutes the first time) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if %errorlevel%==0 goto done

echo.
echo === Native build was not possible on this PC. Building the portable version instead ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Portable
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
