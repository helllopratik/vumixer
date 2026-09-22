@echo off
rem Build the Windows VU Mixer (self-contained single-file exe). Windows hosts.
cd /d "%~dp0"
dotnet publish src\VUMixer -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 exit /b 1
echo.
echo done: publish\VUMixer.exe — copy it to any Windows 10/11 machine and double-click.