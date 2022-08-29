:: ARM64, x64, win32
if not defined ARCH (
  echo Please define 'ARCH' to build.
  exit /b 1
)

:: create directory
set BUILD_DIR=%SRC_DIR%\build\cmake\build
rmdir /S /Q %BUILD_DIR%
mkdir %BUILD_DIR%

:: build
pushd %BUILD_DIR%
  cmake -DCMAKE_BUILD_TYPE=Release -G "Visual Studio 17 2022" -A %ARCH% ..
  cmake --build . --config Release
popd
