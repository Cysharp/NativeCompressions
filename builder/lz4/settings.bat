set SRC_DIR=lz4
set LIBNAME=liblz4
set EXENAME=lz4

cd %SRC_DIR%
  FOR /F "tokens=* USEBACKQ" %%F IN (`git tag --points-at HEAD`) DO (SET VERSION=%%F)
  set GIT_VERSION=%VERSION%
  :: 'v1.5.2 ' -> '1.5.2'
  set FILE_VERSION=%VERSION:~1%
cd ..
