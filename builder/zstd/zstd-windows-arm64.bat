@echo OFF
SETLOCAL ENABLEDELAYEDEXPANSION

set SCRIPT_DIR=%~dp0
call %SCRIPT_DIR%\settings.bat
set OS=windows
set ARCH=ARM64
set PLATFORM=arm64
if not defined OUTPUT_DIR (set OUTPUT_DIR=src\runtimes\%OS%-%PLATFORM%)

:: build
call %SCRIPT_DIR%\core\builder-windows.bat
if %ERRORLEVEL% == 1 exit /b 1

:: confirm
dir %SRC_DIR%\build\cmake\build\lib\Release\%EXENAME%*
dir %SRC_DIR%\build\cmake\build\programs\Release\%EXENAME%.exe

:: copy
mkdir %OUTPUT_DIR%
cp %SRC_DIR%\build\cmake\build\lib\Release\%EXENAME%.dll .\%OUTPUT_DIR%\%EXENAME%.dll
@REM cp %SRC_DIR%\build\cmake\build\programs\Release\%EXENAME%.exe .\%OUTPUT_DIR%\%EXENAME%.exe
