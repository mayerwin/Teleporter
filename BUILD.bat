@echo off
REM Teleporter - single-command build. Output: dist\Teleporter.exe
REM Self-contained, trimmed, compressed single-file Windows x64 exe (no .NET install needed).
REM Prereq: .NET 10 SDK  (winget install Microsoft.DotNet.SDK.10)
setlocal
pushd "%~dp0"
where dotnet >nul 2>nul || (echo [ERROR] dotnet not found & popd & exit /b 1)
REM A running copy (often hidden in the tray) locks the exe.
taskkill /IM Teleporter.exe /F >nul 2>&1
dotnet publish src\Teleporter\Teleporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o dist || goto :fail
REM SkiaSharp/HarfBuzz ship native symbol files (~100 MB) that the exe does not need.
del /q dist\*.pdb >nul 2>&1
echo Build OK: dist\Teleporter.exe
popd
exit /b 0
:fail
echo [ERROR] Build failed.
popd
exit /b 1
