#!/bin/bash

set -e

ZSTD_VERSION=$(cd zstd && echo "$(git tag --points-at HEAD | tr -d '[:space:]')" && cd ..)
GIT_ZSTD_VERSION=${ZSTD_VERSION}
FILE_ZSTD_VERSION=$(echo "${ZSTD_VERSION}" | cut -c 2-)
OS=ios
PLATFORM=arm64
OUTPUT_DIR=${OUTPUT_DIR:=src/runtimes/${OS}-${PLATFORM}/}

# build
bash ./builder/zstd/core/zstd-builder-ios.sh

# confirm
ls -l zstd/build/cmake/build/lib/libzstd.a
ls -l zstd/build/cmake/build/lib/libzstd.*dylib

# copy
mkdir -p "./${OUTPUT_DIR}/"
cp zstd/build/cmake/build/lib/libzstd.a "./${OUTPUT_DIR}/."
#cp "zstd/build/cmake/build/lib/libzstd.${FILE_ZSTD_VERSION}.dylib" "./${OUTPUT_DIR}/libzstd.dylib"
