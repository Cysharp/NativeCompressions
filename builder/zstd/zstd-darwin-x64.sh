#!/bin/bash
set -e

SCRIPT_DIR=$(cd $(dirname $0); pwd)
source $SCRIPT_DIR/settings.sh
OS=darwin
PLATFORM=x64
OUTPUT_DIR=${OUTPUT_DIR:=src/runtimes/${OS}-${PLATFORM}/}

# build
. $SCRIPT_DIR/core/builder-darwin-x64.sh

# confirm
ls $SRC_DIR/lib/$LIBNAME.a
ls $SRC_DIR/lib/$LIBNAME.*dylib
#ls $SRC_DIR/programs/$EXENAME

# copy
mkdir -p "./${OUTPUT_DIR}/"
#cp ./$SRC_DIR/lib/$LIBNAME.a "./${OUTPUT_DIR}/."
cp "./$SRC_DIR/lib/$LIBNAME.${FILE_VERSION}.dylib" "./${OUTPUT_DIR}/$LIBNAME.dylib"
#cp ./$SRC_DIR/programs/$EXENAME "./${OUTPUT_DIR}/."
