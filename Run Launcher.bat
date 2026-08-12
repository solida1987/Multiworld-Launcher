@echo off
rem Builds the current source, then launches the freshly-built exe.
rem (The publish\ folder is only refreshed by "dotnet publish" and can be
rem  days old — never launch from it when checking current work.)
cd /d "%~dp0"
echo Building latest...
dotnet build -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo BUILD FAILED — fix errors above, nothing launched.
    pause
    exit /b 1
)
start "" "bin\Release\net8.0-windows\win-x64\Multiworld Launcher.exe"
