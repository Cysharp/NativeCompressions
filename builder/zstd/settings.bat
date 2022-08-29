set SRC_DIR=zstd
set LIBNAME=libzstd
set EXENAME=zstd

cd %SRC_DIR%
  FOR /F "tokens=* USEBACKQ" %%F IN (`git tag --points-at HEAD`) DO (SET VERSION=%%F)
  set GIT_VERSION=%VERSION%
  :: 'v1.5.2 ' -> '1.5.2'
  set FILE_VERSION=%VERSION:~1%
cd ..
