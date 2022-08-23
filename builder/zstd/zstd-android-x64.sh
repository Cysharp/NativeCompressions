#!/bin/sh

set -e

ZSTD_VERSION=$(cd zstd && echo "$(git tag --points-at HEAD | tr -d '[:space:]')" && cd ..)
GIT_ZSTD_VERSION=${ZSTD_VERSION}
FILE_ZSTD_VERSION=$(echo "${ZSTD_VERSION}" | cut -c 2-)
OS=android
ABI=x86_64
PLATFORM=x64
OUTPUT_DIR=${OUTPUT_DIR:=pkg/zstd/${GIT_ZSTD_VERSION}/${OS}/${PLATFORM}/}

# build
docker run --rm -v "$PWD/builder/zstd/core:/builder" -v "$PWD/zstd:/src" -e "ABI=$ABI" ubuntu:22.04 /bin/bash /builder/zstd-builder-android.sh

# confirm
ls zstd/build/cmake/build/lib/libzstd.a
ls zstd/build/cmake/build/lib/libzstd.so*
#ls zstd/build/cmake/build/programs/zstd

# copy
mkdir -p "./${OUTPUT_DIR}/"
#cp ./zstd/build/cmake/build/lib/libzstd.a "./${OUTPUT_DIR}/."
cp "./zstd/build/cmake/build/lib/libzstd.so" "./${OUTPUT_DIR}/."
#cp ./zstd/build/cmake/build/programs/zstd "./${OUTPUT_DIR}/."
