#!/bin/bash
set -e

SCRIPT_DIR=$(cd $(dirname $0); pwd)
source $SCRIPT_DIR/settings.sh
OS=ios
PLATFORM=arm64
OUTPUT_DIR=${OUTPUT_DIR:=src/runtimes/${OS}-${PLATFORM}/}

# build
. $SCRIPT_DIR/core/builder-ios.sh

# confirm
ls -l $SRC_DIR/build/cmake/build/lib/$LIBNAME.a
ls -l $SRC_DIR/build/cmake/build/lib/$LIBNAME.*dylib

# copy
mkdir -p "./${OUTPUT_DIR}/"
cp "$SRC_DIR/build/cmake/build/lib/$LIBNAME.a" "./${OUTPUT_DIR}/."
#cp "$SRC_DIR/build/cmake/build/lib/$LIBNAME.${FILE_VERSION}.dylib" "./${OUTPUT_DIR}/$LIBNAME.dylib"
