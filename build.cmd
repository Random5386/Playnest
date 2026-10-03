@echo off
rem Builds Playnest and writes the result to build.log (Claude reads this file to fix any errors).
cd /d "%~dp0"
rem Turn off Avalonia's anonymous build telemetry
set AVALONIA_TELEMETRY_OPTOUT=1

rem A running Playnest (it may be hidden in the tray) locks its own files, so close it first.
taskkill /im Playnest.exe /f >nul 2>&1
rem Stop background compiler processes left over from earlier builds (they can keep build files locked).
dotnet build-server shutdown >nul 2>&1

rem Start from a clean slate so old or blocked build files can't break the build.
if exist "src\Playnest\bin\Debug" rmdir /s /q "src\Playnest\bin\Debug" >nul 2>&1
if exist "src\Playnest\obj\Debug" rmdir /s /q "src\Playnest\obj\Debug" >nul 2>&1

echo Building Playnest... (this takes about a minute the first time)
dotnet build Playnest.sln -c Debug -nologo -v minimal > build.log 2>&1
if %errorlevel%==0 (
  echo BUILD SUCCEEDED>> build.log
  echo.
  echo Build succeeded. Starting Playnest...
  start "" "src\Playnest\bin\Debug\net10.0\Playnest.exe"
) else (
  echo BUILD FAILED>> build.log
  findstr /c:"is denied" /c:"being used by another process" build.log >nul 2>&1 && (
    echo NOTE: Windows refused access to a build file. This is usually the antivirus blocking it.>> build.log
    echo.
    echo Windows refused access to a build file - most likely your antivirus is blocking it.
    echo Add an exception for this folder in your antivirus, then run build.cmd again.
  )
  echo.
  echo Build failed - Claude can read build.log to fix it.
  pause
)
