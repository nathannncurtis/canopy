@echo off
setlocal

set PROJECT_DIR=%~dp0
set /p VERSION=<"%PROJECT_DIR%version.txt"
set ARCH=%~1
if "%ARCH%"=="" set ARCH=x64
if /I "%ARCH%"=="x64" (
    set CMAKE_ARCH=x64
    set RID=win-x64
    set BUILD_DIR=%PROJECT_DIR%dist\Canopy
) else if /I "%ARCH%"=="arm64" (
    set CMAKE_ARCH=ARM64
    set RID=win-arm64
    set BUILD_DIR=%PROJECT_DIR%dist\Canopy-arm64
) else (
    echo Usage: build.bat [x64^|arm64]
    exit /b 2
)

echo === Building C++ engine (Release %ARCH%) ===
cmake -S "%PROJECT_DIR%Core" -B "%PROJECT_DIR%Core\build-%ARCH%" -A %CMAKE_ARCH% -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 goto fail
cmake --build "%PROJECT_DIR%Core\build-%ARCH%" --config Release
if errorlevel 1 goto fail

echo === Publishing WPF app (%RID%, v%VERSION%) ===
dotnet publish "%PROJECT_DIR%App\SizeMonitor.App.csproj" -c Release -r %RID% --self-contained true -p:Version=%VERSION% -o "%BUILD_DIR%"
if errorlevel 1 goto fail

copy /Y "%PROJECT_DIR%Core\build-%ARCH%\bin\Release\Canopy.Core.dll" "%BUILD_DIR%\Canopy.Core.dll"
if errorlevel 1 (
    copy /Y "%PROJECT_DIR%Core\build-%ARCH%\bin\Canopy.Core.dll" "%BUILD_DIR%\Canopy.Core.dll"
)
copy /Y "%PROJECT_DIR%version.txt" "%BUILD_DIR%\version.txt"

echo.
echo === Build complete (UNSIGNED) ===
echo Output: %BUILD_DIR%
pause
exit /b 0

:fail
echo.
echo BUILD FAILED
pause
exit /b 1
