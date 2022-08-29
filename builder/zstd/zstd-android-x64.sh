#!/bin/sh
set -e

SCRIPT_DIR=$(cd $(dirname $0); pwd)
source $SCRIPT_DIR/settings.sh
OS=android
ABI=x86_64
PLATFORM=x64
OUTPUT_DIR=${OUTPUT_DIR:=src/runtimes/${OS}-${PLATFORM}/}

# build
docker run --rm -v "$SCRIPT_DIR/core:/builder" -v "$PWD/$SRC_DIR:/src" -e "ABI=$ABI" ubuntu:22.04 /bin/bash /builder/builder-android.sh

# confirm
ls $SRC_DIR/build/cmake/build/lib/$LIBNAME.a
ls $SRC_DIR/build/cmake/build/lib/$LIBNAME.so*
ls $SRC_DIR/build/cmake/build/programs/$EXENAME

# copy
mkdir -p "./${OUTPUT_DIR}/"
#cp ./$SRC_DIR/build/cmake/build/lib/$LIBNAME.a "./${OUTPUT_DIR}/."
cp ./$SRC_DIR/build/cmake/build/lib/$LIBNAME.so "./${OUTPUT_DIR}/."
#cp ./$SRC_DIR/build/cmake/build/programs/$EXENAME "./${OUTPUT_DIR}/."
