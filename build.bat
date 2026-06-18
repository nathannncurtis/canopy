@echo off
setlocal

set PROJECT_DIR=%~dp0
set /p VERSION=<"%PROJECT_DIR%version.txt"
set BUILD_DIR=%PROJECT_DIR%dist\Canopy

echo === Building C++ engine (Release x64) ===
cmake -S "%PROJECT_DIR%Core" -B "%PROJECT_DIR%Core\build" -A x64 -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 goto fail
cmake --build "%PROJECT_DIR%Core\build" --config Release
if errorlevel 1 goto fail

echo === Publishing WPF app (win-x64, v%VERSION%) ===
dotnet publish "%PROJECT_DIR%App\SizeMonitor.App.csproj" -c Release -r win-x64 --self-contained true -p:Version=%VERSION% -o "%BUILD_DIR%"
if errorlevel 1 goto fail

copy /Y "%PROJECT_DIR%Core\build\bin\Release\Canopy.Core.dll" "%BUILD_DIR%\Canopy.Core.dll"
if errorlevel 1 (
    copy /Y "%PROJECT_DIR%Core\build\bin\Canopy.Core.dll" "%BUILD_DIR%\Canopy.Core.dll"
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
