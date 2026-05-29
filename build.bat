@echo off
REM ===========================================================================
REM FFXI Macro Manager - one-shot build
REM
REM Compiles src\FFXIMacroManager.csproj into bin\Release\FFXIMacroManager.exe
REM using the .NET SDK's `dotnet msbuild`. After a successful build, also
REM copies the fresh .exe up to the repo root so the desktop shortcut and
REM anyone running from the cloned folder picks up the latest build.
REM
REM Requires:
REM   - .NET 8+ SDK (https://aka.ms/dotnet/download or `winget install
REM     Microsoft.DotNet.SDK.8`)
REM   - .NET Framework 4.8.1 Developer Pack (`winget install
REM     Microsoft.DotNet.Framework.DeveloperPack_4`)
REM
REM Legacy note: the original build.bat tried to hardcode a Visual Studio
REM MSBuild path. That broke when VS wasn't installed and was unable to load
REM the modern Roslyn compiler the source needs. `dotnet msbuild` solves
REM both: it ships its own Roslyn and the WindowsDesktop SDK's WinFX.targets
REM (imported via the csproj fallback chain) handle XAML compilation.
REM ===========================================================================

setlocal
pushd "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet not found on PATH. Install the .NET 8 SDK:
    echo     winget install Microsoft.DotNet.SDK.8
    exit /b 1
)

REM TargetFrameworkVersion override matches the v4.8.1 dev pack we ask the
REM user to install above. The csproj declares v4.7.2 but 4.8.1 is a strict
REM superset and no APIs from later than 4.7.2 are referenced.
dotnet msbuild src\FFXIMacroManager.csproj ^
    -p:Configuration=Release ^
    -p:TargetFrameworkVersion=v4.8.1 ^
    -v:minimal -nologo
set EC=%ERRORLEVEL%

if "%EC%"=="0" (
    REM Sync the freshly-built exe to the repo root so the desktop
    REM shortcut and any clone-and-run users get the latest binary
    REM without having to dig into src\bin\Release.
    copy /Y src\bin\Release\FFXIMacroManager.exe FFXIMacroManager.exe >nul
    echo.
    echo Build succeeded.  Deployed:
    echo     %~dp0FFXIMacroManager.exe
) else (
    echo.
    echo Build FAILED with exit code %EC%.
)

popd
endlocal & exit /b %EC%
