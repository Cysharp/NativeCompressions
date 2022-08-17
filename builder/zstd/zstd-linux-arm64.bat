:: run on cmd
SETLOCAL ENABLEDELAYEDEXPANSION
cd zstd
FOR /F "tokens=* USEBACKQ" %%F IN (`git tag --points-at HEAD`) DO ( SET ZSTD_VERSION=%%F )
cd ..
:: 'v1.5.2 ' -> 'v1.5.2'
set GIT_ZSTD_VERSION=%ZSTD_VERSION:~0,-1%
:: 'v1.5.2 ' -> '1.5.2'
set FILE_ZSTD_VERSION=%ZSTD_VERSION:~1,-1%
set OS=linux
set PLATFORM=arm64
if not defined OUTPUT_DIR (set OUTPUT_DIR=src\runtimes\%OS%-%PLATFORM%)

:: build
docker run --rm -v "%cd%/builder/zstd/docker:/builder" -v "%cd%/zstd:/src" ubuntu:22.04 /bin/bash /builder/zstd-builder-linux-arm64.sh

:: confirm
dir zstd\lib\libzstd.a
dir zstd\lib\libzstd.so*

:: copy
mkdir %OUTPUT_DIR%
cp .\zstd\lib\libzstd.a .\%OUTPUT_DIR%\libzstd.a
cp .\zstd\lib\libzstd.so.%FILE_ZSTD_VERSION% .\%OUTPUT_DIR%\libzstd.so
::cp .\zstd\programs\zstd .\%OUTPUT_DIR%\zstd
