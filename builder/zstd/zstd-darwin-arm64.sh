#!/bin/sh

set -e

# generate arm64 on M1/M2, x64 on Intel or Rosetta2.

ZSTD_VERSION=$(cd zstd && echo "$(git tag --points-at HEAD | tr -d '[:space:]')" && cd ..)
GIT_ZSTD_VERSION=${ZSTD_VERSION}
FILE_ZSTD_VERSION=$(echo "${ZSTD_VERSION}" | cut -c 2-)
OS=osx
PLATFORM=arm64
OUTPUT_DIR=${OUTPUT_DIR:=src/runtimes/${OS}-${PLATFORM}/}

# build
cd zstd
make clean
make CFLAGS="-target arm64-apple-macos11" lib # github action alrady include x86_64 LZ4/LZMA and failed on arm64.
cd ..

# confirm
ls zstd/lib/libzstd.a
ls zstd/lib/libzstd.*dylib
#ls zstd/programs/zstd

# copy
mkdir -p "./${OUTPUT_DIR}/"
cp ./zstd/lib/libzstd.a "./${OUTPUT_DIR}/."
cp "./zstd/lib/libzstd.${FILE_ZSTD_VERSION}.dylib" "./${OUTPUT_DIR}/libzstd.dylib"
#cp ./zstd/programs/zstd "./${OUTPUT_DIR}/."
