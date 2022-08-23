#!/bin/bash

set -eu

NATIVE_OS_KIND=$(uname | tr "[:upper:]" "[:lower:]") # should be darwin
BUILD_TYPE=Release # Release or Debug

CMAKE_DIR="$(pwd)/zstd/build/cmake"
BUILD_DIR="${CMAKE_DIR}/build"
CMAKE_TOOLCHAIN_FILE="$(pwd)/builder/zstd/ios-arm64.toolchain.cmake"

# NOTE: zlib and lzma will found from iPhoneOS.sdk.
# -- Found ZLIB: /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk/usr/lib/libz.tbd (found version "1.2.11")
# -- Found LibLZMA: /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk/usr/lib/liblzma.tbd (found version "5.2.5")

COLOR_RED='\033[0;31m'          # Red
COLOR_GREEN='\033[0;32m'        # Green
COLOR_YELLOW='\033[0;33m'       # Yellow
COLOR_BLUE='\033[0;94m'         # Blue
COLOR_PURPLE='\033[0;35m'       # Purple
COLOR_OFF='\033[0m'             # Reset

step() {
    STEP_NUM=$((${STEP_NUM-0} + 1))
    STEP_MESSAGE="$*"
    printf '\n'
    printf '%b\n' "${COLOR_PURPLE}=>> STEP ${STEP_NUM} : ${STEP_MESSAGE} ${COLOR_OFF}"

    unset STEP2_NUM # reset
}
step2() {
    STEP2_NUM=$((${STEP2_NUM-0} + 1))
    STEP2_MESSAGE="$*"
    printf '\n'
    printf '%b\n' "${COLOR_BLUE}>>> STEP ${STEP_NUM}.${STEP2_NUM} : ${STEP2_MESSAGE} ${COLOR_OFF}"
}
die() {
  printf '%b\n' "${COLOR_RED}💔  $*${COLOR_OFF}" >&2
  exit 1
}

__clean() {
  step "clean working space."

  rm -rf "${BUILD_DIR}"
  mkdir -p "${BUILD_DIR}"
}

# __find_build_toolchains zstd iPhoneOS/arm64/8.0
__find_build_toolchains() {
  step "Find build toolchains."

  if [ "$NATIVE_OS_KIND" != 'darwin' ] ; then
    die "this software can only be run on macOS."
  fi

  TARGET_OS_NAME=$(printf '%s\n' "$2" | cut -d/ -f1)
  TARGET_OS_VERS=$(printf '%s\n' "$2" | cut -d/ -f3)
  TARGET_OS_ARCH=$(printf '%s\n' "$2" | cut -d/ -f2)
  TARGET_OS_NAME_LOWER_CASE="$(echo "$TARGET_OS_NAME" | tr "[:upper:]" "[:lower:]")"

  PACKAGE_CDEFINE="__arm64__"

  # should be "/Applications/Xcode.app/Contents/Developer"
  TOOLCHAIN_ROOT="$(xcode-select -p)"

  [ -z "$TOOLCHAIN_ROOT" ] && die "please run command 'xcode-select --switch DIR', then try again."
  [ -d "$TOOLCHAIN_ROOT" ] || die "TOOLCHAIN_ROOT=$TOOLCHAIN_ROOT directory is not exist."

  TOOLCHAIN_BIND="$TOOLCHAIN_ROOT/Toolchains/XcodeDefault.xctoolchain/usr/bin"
  SYSROOT="$TOOLCHAIN_ROOT/Platforms/${TARGET_OS_NAME}.platform/Developer/SDKs/${TARGET_OS_NAME}.sdk"
  SYSTEM_LIBRARY_DIR="$SYSROOT/usr/lib"

  CC="$TOOLCHAIN_BIND/clang"
  CXX="$TOOLCHAIN_BIND/clang++"

  CCFLAGS="-isysroot $SYSROOT -arch $TARGET_OS_ARCH -m${TARGET_OS_NAME_LOWER_CASE}-version-min=$TARGET_OS_VERS -Qunused-arguments -Os -pipe"
  CPPFLAGS="-isysroot $SYSROOT -Qunused-arguments"
  LDFLAGS="-isysroot $SYSROOT -arch $TARGET_OS_ARCH -m${TARGET_OS_NAME_LOWER_CASE}-version-min=$TARGET_OS_VERS"

  if [[ "$BUILD_TYPE" == "Release" ]] ; then
    CCFLAGS="$CCFLAGS -Wl,-S -Os -DNDEBUG"
    LDFLAGS="$LDFLAGS -Wl,-S"
  fi

  for item in $PACKAGE_CDEFINE; do
    CPPFLAGS="$CPPFLAGS -D$item"
  done

  CXXFLAGS="$CCFLAGS"
}

__config_cmake_variables() {
  step "Config cmake variables."

  CMAKE_VERBOSE_MAKEFILE=ON
  CMAKE_COLOR_MAKEFILE=ON

  BUILD_SHARED_LIBS=ON

  CMAKE_BUILD_TYPE=$BUILD_TYPE

  CMAKE_SYSTEM_NAME=Darwin
  CMAKE_SYSTEM_VERSION=8.0
  CMAKE_SYSTEM_PROCESSOR=arm64

  CMAKE_ASM_COMPILER="$CC"
  CMAKE_ASM_FLAGS="-arch arm64"

  CMAKE_C_COMPILER="$CC"
  CMAKE_C_FLAGS="$CCFLAGS $CPPFLAGS $LDFLAGS"

  CMAKE_CXX_COMPILER="$CXX"
  CMAKE_CXX_FLAGS="$CXXFLAGS $CPPFLAGS $LDFLAGS"

  CMAKE_AR="$TOOLCHAIN_BIND/ar"
  CMAKE_NM="$TOOLCHAIN_BIND/nm"
  CMAKE_RANLIB="$TOOLCHAIN_BIND/ranlib"
  CMAKE_STRIP="$TOOLCHAIN_BIND/strip"

  CMAKE_OSX_SYSROOT="$SYSROOT"

  # https://cmake.org/cmake/help/latest/variable/CMAKE_OSX_ARCHITECTURES.html
  CMAKE_OSX_ARCHITECTURES="${TARGET_OS_ARCH}\" CACHE STRING \""

  CMAKE_FIND_DEBUG_MODE=OFF

  CMAKE_LIBRARY_PATH="$SYSTEM_LIBRARY_DIR"
}

__create_cmake_toolchain_file() {
  step "Create toolchain file."

  cat <<EOF | tee "${CMAKE_TOOLCHAIN_FILE}"
set(CMAKE_VERBOSE_MAKEFILE $CMAKE_VERBOSE_MAKEFILE)
set(CMAKE_COLOR_MAKEFILE   $CMAKE_COLOR_MAKEFILE)

set(BUILD_SHARED_LIBS $BUILD_SHARED_LIBS)

set(CMAKE_BUILD_TYPE  $CMAKE_BUILD_TYPE)

set(CMAKE_SYSTEM_NAME      $CMAKE_SYSTEM_NAME)
set(CMAKE_SYSTEM_VERSION   $CMAKE_SYSTEM_VERSION)
set(CMAKE_SYSTEM_PROCESSOR $CMAKE_SYSTEM_PROCESSOR)

set(CMAKE_ASM_COMPILER $CMAKE_ASM_COMPILER)
set(CMAKE_ASM_FLAGS "$CMAKE_ASM_FLAGS")

set(CMAKE_C_COMPILER "$CMAKE_C_COMPILER")
set(CMAKE_C_FLAGS "$CMAKE_C_FLAGS")

set(CMAKE_CXX_COMPILER "$CMAKE_CXX_COMPILER")
set(CMAKE_CXX_FLAGS "$CMAKE_CXX_FLAGS")

set(CMAKE_AR     "$CMAKE_AR")
set(CMAKE_NM     "$CMAKE_NM")
set(CMAKE_RANLIB "$CMAKE_RANLIB")
set(CMAKE_STRIP  "$CMAKE_STRIP")

set(CMAKE_OSX_SYSROOT "$CMAKE_OSX_SYSROOT")

set(CMAKE_OSX_ARCHITECTURES "${CMAKE_OSX_ARCHITECTURES}")

set(CMAKE_FIND_DEBUG_MODE $CMAKE_FIND_DEBUG_MODE)

set(CMAKE_LIBRARY_PATH "$CMAKE_LIBRARY_PATH")
EOF
}
