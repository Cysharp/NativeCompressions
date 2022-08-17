#!/bin/bash

set -e

ZSTD_VERSION=$(cd zstd && echo "$(git tag --points-at HEAD | tr -d '[:space:]')" && cd ..)
GIT_ZSTD_VERSION=${ZSTD_VERSION}
FILE_ZSTD_VERSION=$(echo "${ZSTD_VERSION}" | cut -c 2-)
OS=ios
PLATFORM=arm64
OUTPUT_DIR=${OUTPUT_DIR:=src/runtimes/${OS}-${PLATFORM}/}

PACKAGE_NAME=zstd
TARGET=iPhoneOS/arm64/8.0

# prepare
source ./builder/zstd/docker/zstd-builder-ios-arm64.sh
__clean
__find_build_toolchains "${PACKAGE_NAME}" "${TARGET}"
__config_cmake_variables
__create_cmake_toolchain_file

# build
pushd "${WORKING_DIR_BASE}"
cmake -Wno-dev -S "${WORKING_DIR_CMAKE}" -B "${WORKING_DIR_BUILD}" -DCMAKE_INSTALL_PREFIX="./tmp" -DCMAKE_TOOLCHAIN_FILE="${CMAKE_TOOLCHAIN_FILE}" -DCMAKE_VERBOSE_MAKEFILE=ON -DCMAKE_COLOR_MAKEFILE=ON -DZSTD_MULTITHREAD_SUPPORT=ON -DZSTD_BUILD_TESTS=OFF -DZSTD_BUILD_CONTRIB=OFF -DZSTD_BUILD_PROGRAMS=ON -DZSTD_BUILD_STATIC=ON -DZSTD_BUILD_SHARED=ON -DZSTD_LZ4_SUPPORT=OFF -DZSTD_ZLIB_SUPPORT=ON -DZSTD_LZMA_SUPPORT=ON
cmake --build "${WORKING_DIR_BUILD}"  -- -j8
popd

# confirm
ls -l "${WORKING_DIR_BUILD}/lib/libzstd.a"
ls -l ${WORKING_DIR_BUILD}/lib/libzstd.*dylib

# copy
mkdir -p "./${OUTPUT_DIR}/"
cp "${WORKING_DIR_BUILD}/lib/libzstd.a" "./${OUTPUT_DIR}/."
cp "${WORKING_DIR_BUILD}/lib/libzstd.${FILE_ZSTD_VERSION}.dylib" "./${OUTPUT_DIR}/libzstd.dylib"
