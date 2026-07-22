@echo off
cd /d "%~dp0"
start "" dotnet run --project "%~dp0DesktopZones.csproj"
