@echo off
REM ===========================================================================
REM FFXI Macro Manager — one-shot build
REM
REM Compiles src\FFXIMacroManager.csproj into bin\Release\FFXIMacroManager.exe
REM using the Visual Studio MSBuild bundled with this machine. The data/
REM JSON files are copied next to the .exe automatically by the project file.
REM ===========================================================================

setlocal

set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    REM Try locating with vswhere
    for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -property installationPath`) do (
        set "VS_PATH=%%i"
    )
    if defined VS_PATH set "MSBUILD=%VS_PATH%\MSBuild\Current\Bin\MSBuild.exe"
)

if not exist "%MSBUILD%" (
    echo MSBuild.exe not found. Install the .NET desktop development workload
    echo through Visual Studio Installer, or install the .NET 4.8 Developer
    echo Pack, then re-run this script.
    exit /b 1
)

pushd "%~dp0"
"%MSBUILD%" src\FFXIMacroManager.csproj /p:Configuration=Release /v:minimal /nologo
set EC=%ERRORLEVEL%
popd

if "%EC%"=="0" (
    echo.
    echo Build succeeded.  Run:
    echo     "%~dp0src\bin\Release\FFXIMacroManager.exe"
) else (
    echo.
    echo Build FAILED with exit code %EC%.
)

endlocal & exit /b %EC%
