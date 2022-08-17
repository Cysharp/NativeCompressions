:: run on cmd
SETLOCAL ENABLEDELAYEDEXPANSION
cd zstd
FOR /F "tokens=* USEBACKQ" %%F IN (`git tag --points-at HEAD`) DO ( SET ZSTD_VERSION=%%F )
cd ..
:: 'v1.5.2 ' -> 'v1.5.2'
set GIT_ZSTD_VERSION=%ZSTD_VERSION:~0,-1%
:: 'v1.5.2 ' -> '1.5.2'
set FILE_ZSTD_VERSION=%ZSTD_VERSION:~1,-1%
set OS=win
set PLATFORM=arm64
if not defined OUTPUT_DIR (set OUTPUT_DIR=src\runtimes\%OS%-%PLATFORM%)

:: build
set BASEDIR=%cd%
rmdir /S /Q %BASEDIR%\zstd\build\cmake\build
mkdir %BASEDIR%\zstd\build\cmake\build
cd %BASEDIR%/zstd/build/cmake/build
cmake -DCMAKE_BUILD_TYPE=Release -G "Visual Studio 17 2022" -A ARM64 ..
cmake --build . --config Release
cd %BASEDIR%

:: confirm
dir zstd\build\cmake\build\lib\Release\zstd*
dir zstd\build\cmake\build\programs\Release\zstd.exe

:: copy
mkdir %OUTPUT_DIR%
cp zstd\build\cmake\build\lib\Release\zstd.dll .\%OUTPUT_DIR%\libzstd.dll
cp zstd\build\cmake\build\programs\Release\zstd.exe .\%OUTPUT_DIR%\zstd.exe
