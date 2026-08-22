@echo off
setlocal
title PC Boost - Build Windows EXE

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo .NET 8 SDK is not installed.
  echo Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this file again.
  pause
  exit /b 1
)

echo.
echo Building PC Boost for Windows x64...
dotnet publish "%~dp0PCBoostOptimizer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish"
if errorlevel 1 (
  echo.
  echo Build failed. Check the error message above.
  pause
  exit /b 1
)

echo.
echo Done: publish\PCBoostOptimizer.exe
start "" "%~dp0publish\PCBoostOptimizer.exe"
endlocal
