#!/bin/bash
set -eu

NATIVE_OS_KIND=$(uname | tr "[:upper:]" "[:lower:]") # should be linux
BUILD_TYPE=Release # Release or Debug

# armeabi-v7a, arm64-v8a, x86, x86_64
ABI="${ABI:=armeabi-v7a}"
ANDROID_NDK=${ANDROID_NDK:=android-ndk-r23c}
ANDROID_PLATFORM=${ANDROID_PLATFORM:=21}
ANDROID_URL=https://dl.google.com/android/repository/${ANDROID_NDK}-linux.zip

ZLIB=${ZLIB:=1.2.12}
ZLIB_URL=https://zlib.net/zlib-${ZLIB}.tar.gz

XZ=${XZ:=5.2.5}
XZ_URL=https://downloads.sourceforge.net/project/lzmautils/xz-${XZ}.tar.gz

ANDROID_NDK_HOME="/root/${ANDROID_NDK}"
INSTALL_DIR="/root/install/android/${ANDROID_PLATFORM}"
LIB_WORKING_DIR="/root/tmp"
TMP_SOURCE_DIR=/tmp/source

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
  rm -rf "${LIB_WORKING_DIR:=/root/tmp}"
  rm -rf "${TMP_SOURCE_DIR:=/tmp/source}"
  mkdir -p ${INSTALL_DIR}
  mkdir -p ${LIB_WORKING_DIR}
  mkdir -p ${TMP_SOURCE_DIR}
}

__install_required_packages() {
  step "Install required packages."

  apt-get update
  apt-get install -yq --no-install-suggests --no-install-recommends build-essential pkg-config curl libssl-dev zlib1g-dev
  apt-get install -yq --no-install-suggests --no-install-recommends unzip cmake ccache
}

__install_android_ndk() {
  step "Install Android NDK."

  if [[ ! -d "${ANDROID_NDK_HOME}/build/cmake" ]]; then
    curl --retry 20 --retry-delay 30 -Lks -o "${TMP_SOURCE_DIR}/${ANDROID_NDK}.zip" "${ANDROID_URL}"
    unzip -q ${TMP_SOURCE_DIR}/${ANDROID_NDK}.zip -d "/root/"
  fi
}

__find_abi_kind() {
  step "Find ABI Kind."

  if [[ -z ${ABI} || "${ABI}" == "" ]]; then
    die "Please defnine variables ABI."
  fi

  if [[ -z ${ANDROID_NDK} || "${ANDROID_NDK}" == "" ]]; then
    die "Please defnine variables ANDROID_NDK."
  fi

  if [[ -z ${ANDROID_PLATFORM} || "${ANDROID_PLATFORM}" == "" ]]; then
    die "Please defnine variables ANDROID_PLATFORM."
  fi

  case ${ABI} in
    "armeabi-v7a")
        SYSTEM_PROCESSOR=armv7-a
        ABI_SHORT=armv7a
        SYSTEM_LIB_ARCH=arm
        CC_ABI=androideabi;;
    "arm64-v8a")
        SYSTEM_PROCESSOR=aarch64
        ABI_SHORT=aarch64
        SYSTEM_LIB_ARCH=aarch64
        CC_ABI=android;;
    "x86")
        SYSTEM_PROCESSOR=i686
        ABI_SHORT=i686
        SYSTEM_LIB_ARCH=i686
        CC_ABI=android;;
    "x86_64")
        SYSTEM_PROCESSOR=x86_64
        ABI_SHORT=x86_64
        SYSTEM_LIB_ARCH=x86_64
        CC_ABI=android;;
    *)
        die "${ABI} is out of range."
esac
}

find_build_toolchains() {
  step2 "Find build toolchains."

  if [ "$NATIVE_OS_KIND" != 'linux' ] ; then
    die "this software can only be run on linux."
  fi

  SYSROOT="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/sysroot"
  SYSTEM_LIBRARY_DIR="${SYSROOT}/usr/lib/${SYSTEM_LIB_ARCH}-linux-${CC_ABI}/${ANDROID_PLATFORM}"

  XZ_INSTALL_DIR="${INSTALL_DIR}/xz/${ABI}"
  ZLIB_INSTALL_DIR="${INSTALL_DIR}/zlib/${ABI}"
  ZSTD_INSTALL_DIR="${INSTALL_DIR}/zstd/${ABI}"

  CMAKE_TOOLCHAIN="${ANDROID_NDK_HOME}/build/cmake/android.toolchain.cmake"

  CC="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/bin/${ABI_SHORT}-linux-${CC_ABI}${ANDROID_PLATFORM}-clang"
  CXX="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/bin/${ABI_SHORT}-linux-${CC_ABI}${ANDROID_PLATFORM}-clang++"
  CPP="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/bin/${ABI_SHORT}-linux-${CC_ABI}${ANDROID_PLATFORM}-clang -E"
  AR="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-ar"
  RANLIB="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-ranlib"
  STRIP="${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip"

  CCFLAGS="--sysroot ${SYSROOT} -Qunused-arguments -fPIC -Wl,--as-needed"
  CPPFLAGS="--sysroot ${SYSROOT} -Qunused-arguments -I${ANDROID_NDK_HOME}/toolchains/llvm/prebuilt/linux-x86_64/sysroot/usr/include/${SYSTEM_LIB_ARCH}-linux-${CC_ABI}"
  LDFLAGS="--sysroot ${SYSROOT} -L${SYSTEM_LIBRARY_DIR} -Wl,--as-needed"

  for item in $PACKAGE_INCLUDES; do
    CPPFLAGS="-I${item} $CPPFLAGS"
  done

  if [[ "$BUILD_TYPE" == "Release" ]] ; then
    CCFLAGS="$CCFLAGS -Wl,--strip-debug -Os -DNDEBUG"
    LDFLAGS="$LDFLAGS -Wl,--strip-debug"
  fi

  CXXFLAGS="${CCFLAGS}"
}

print_build_toolchains() {
  step2 "Print build toolchains."

  cat <<EOF
        BUILD_TYPE = ${BUILD_TYPE}

    NATIVE_OS_KIND = ${NATIVE_OS_KIND}

       ANDROID_NDK = ${ANDROID_NDK}
  ANDROID_NDK_HOME = ${ANDROID_NDK_HOME}
  ANDROID_PLATFORM = ${ANDROID_PLATFORM}
               ABI = ${ABI}

              ZLIB = ${ZLIB}
                XZ = ${XZ}

           SYSROOT = ${SYSROOT}
SYSTEM_LIBRARY_DIR = ${SYSTEM_LIBRARY_DIR}

   CMAKE_TOOLCHAIN = ${CMAKE_TOOLCHAIN}
                CC = ${CC}
               CXX = ${CXX}
               CPP = ${CPP}
                AR = ${AR}
            RANLIB = ${RANLIB}
             STRIP = ${STRIP}

           CCFLAGS = ${CCFLAGS}
          CPPFLAGS = ${CPPFLAGS}
          CXXFLAGS = ${CXXFLAGS}
           LDFLAGS = ${LDFLAGS}

       INSTALL_DIR = ${INSTALL_DIR}
   LIB_WORKING_DIR = ${LIB_WORKING_DIR}
    TMP_SOURCE_DIR = ${TMP_SOURCE_DIR}

    XZ_INSTALL_DIR = ${XZ_INSTALL_DIR}
  ZLIB_INSTALL_DIR = ${ZLIB_INSTALL_DIR}
  ZSTD_INSTALL_DIR = ${ZSTD_INSTALL_DIR}
EOF

}

__install_lib_zlib() {
  step "Install lib zlib."

  # variables
  ROOT_DIR="${LIB_WORKING_DIR}/zlib"
  BUILD_DIR="${ROOT_DIR}/${ABI}/build"
  SRC_DIR="${ROOT_DIR}/src"
  PACKAGE_INCLUDES="${ROOT_DIR}/${ABI}/include ${ROOT_DIR}"

  find_build_toolchains
  print_build_toolchains

  # create directory
  mkdir -p "${SRC_DIR}"
  mkdir -p "${BUILD_DIR}"
  mkdir -p "${ZLIB_INSTALL_DIR}"

  # download
  curl --retry 20 --retry-delay 30 -Lks -o "${TMP_SOURCE_DIR}/zlib.tar.gz" "${ZLIB_URL}"
  tar xf ${TMP_SOURCE_DIR}/zlib.tar.gz -C "${SRC_DIR}" --strip-components 1

  # cmake
  cd ${SRC_DIR}
  /usr/bin/cmake \
    -DCMAKE_TOOLCHAIN_FILE="${CMAKE_TOOLCHAIN}" \
    -DANDROID_ABI="${ABI}" \
    -DANDROID_PLATFORM="${ANDROID_PLATFORM}" \
    -Wno-dev \
    -DCMAKE_EXPORT_COMPILE_COMMANDS=OFF \
    -DBUILD_TESTING=OFF \
    -DCMAKE_BUILD_TYPE="${BUILD_TYPE}" \
    -DCMAKE_VERBOSE_MAKEFILE=ON \
    -DCMAKE_COLOR_MAKEFILE=ON \
    -DCMAKE_SYSTEM_PROCESSOR="${SYSTEM_PROCESSOR}" \
    -DCMAKE_FIND_ROOT_PATH='' \
    -DCMAKE_LIBRARY_PATH="${SYSTEM_LIBRARY_DIR}" \
    -DCMAKE_IGNORE_PATH='' \
    -DCMAKE_INSTALL_PREFIX="${ZLIB_INSTALL_DIR}" \
    -DCMAKE_C_FLAGS="${CCFLAGS} ${CPPFLAGS} ${LDFLAGS}" \
    -DCMAKE_CXX_FLAGS="${CXXFLAGS} ${CPPFLAGS} ${LDFLAGS}" \
    -DBUILD_SHARED_LIBS=ON \
    -DANDROID_TOOLCHAIN=clang \
    -DANDROID_ARM_NEON=TRUE \
    -DANDROID_STL=c++_shared \
    -DANDROID_USE_LEGACY_TOOLCHAIN_FILE=OFF \
    -DNDK_CCACHE=/usr/bin/ccache \
    -S "${SRC_DIR}" \
    -B "${BUILD_DIR}"

  /usr/bin/cmake --build "${BUILD_DIR}" -- -j8
  /usr/bin/cmake --install "${BUILD_DIR}"

  # adjust ELF files for zlib
  ${STRIP} "${ZLIB_INSTALL_DIR}/lib/libz.so"

  # cleanup
  unset ROOT_DIR
  unset BUILD_DIR
  unset SRC_DIR
  unset PACKAGE_INCLUDES
}

__install_lib_xz() {
  step "Install lib xz."

  # variables
  ROOT_DIR="${LIB_WORKING_DIR}/xz"
  BUILD_DIR="${ROOT_DIR}/${ABI}/build"
  SRC_DIR="${ROOT_DIR}/src"
  PACKAGE_INCLUDES="${ROOT_DIR}/${ABI}/include ${ROOT_DIR}"

  find_build_toolchains
  print_build_toolchains

  # create directory
  mkdir -p "${SRC_DIR}"
  mkdir -p "${BUILD_DIR}"
  mkdir -p "${XZ_INSTALL_DIR}"

  # download
  curl --retry 20 --retry-delay 30 -Lk -o "${TMP_SOURCE_DIR}/xz.tar.gz" "${XZ_URL}"
  tar xf ${TMP_SOURCE_DIR}/xz.tar.gz -C "${SRC_DIR}" --strip-components 1

  # cmake
  cd "${BUILD_DIR}"
  ${SRC_DIR}/configure \
    --host="armv7a-linux-androideabi" \
    --prefix="${XZ_INSTALL_DIR}" \
    --disable-option-checking \
    --disable-rpath \
    --disable-nls \
    --enable-largefile \
    CC="${CC}" \
    CFLAGS="${CCFLAGS}" \
    CXX="${CXX}" \
    CXXFLAGS="${CXXFLAGS}" \
    CPP="${CPP}" \
    CPPFLAGS="${CPPFLAGS}" \
    LDFLAGS="${LDFLAGS}" \
    AR="${AR}" \
    RANLIB="${RANLIB}" \
    PKG_CONFIG="/usr/bin/pkg-config" \
    PKG_CONFIG_PATH='' \
    PKG_CONFIG_LIBDIR='' \
    CC_FOR_BUILD='/usr/bin/cc' \
    --disable-debug \
    --enable-static \
    --enable-shared \
    --disable-external-sha256 \
    --disable-werror \
    --enable-assembler \
    --enable-threads=posix

  /usr/bin/gmake -w -C "${BUILD_DIR}" -j8 clean
  /usr/bin/gmake -w -C "${BUILD_DIR}" -j8
  /usr/bin/gmake -w -C "${BUILD_DIR}" -j8 install

  # cleanup
  unset ROOT_DIR
  unset BUILD_DIR
  unset SRC_DIR
  unset PACKAGE_INCLUDES
}

__install_zstd() {
  step "Install zstd."

  # variables
  CMAKE_DIR="/src/build/cmake"
  BUILD_DIR="${CMAKE_DIR}/build"
  PACKAGE_INCLUDES="${XZ_INSTALL_DIR}/include ${ZLIB_INSTALL_DIR}/include"

  find_build_toolchains
  print_build_toolchains

  # create directory
  rm -rf "${BUILD_DIR:=/src/build/cmake/build}"
  mkdir "${BUILD_DIR}"

  # build
  cd ${BUILD_DIR}
  /usr/bin/cmake \
    -DCMAKE_TOOLCHAIN_FILE="${CMAKE_TOOLCHAIN}" \
    -DANDROID_ABI="${ABI}" \
    -DANDROID_PLATFORM=${ANDROID_PLATFORM} \
    -Wno-dev \
    -DCMAKE_EXPORT_COMPILE_COMMANDS=OFF \
    -DBUILD_TESTING=OFF \
    -DCMAKE_BUILD_TYPE="${BUILD_TYPE}" \
    -DCMAKE_VERBOSE_MAKEFILE=ON \
    -DCMAKE_COLOR_MAKEFILE=ON \
    -DCMAKE_SYSTEM_PROCESSOR="${SYSTEM_PROCESSOR}" \
    -DCMAKE_FIND_ROOT_PATH="${ZLIB_INSTALL_DIR};${XZ_INSTALL_DIR}" \
    -DCMAKE_LIBRARY_PATH="${SYSTEM_LIBRARY_DIR}" \
    -DCMAKE_IGNORE_PATH="${XZ_INSTALL_DIR}/bin" \
    -DCMAKE_INSTALL_PREFIX="${ZSTD_INSTALL_DIR}" \
    -DCMAKE_C_FLAGS="${CCFLAGS} ${CPPFLAGS} ${LDFLAGS}" \
    -DCMAKE_CXX_FLAGS="${CXXFLAGS} ${CPPFLAGS} ${LDFLAGS}" \
    -DBUILD_SHARED_LIBS=ON \
    -DANDROID_TOOLCHAIN=clang \
    -DANDROID_ARM_NEON=TRUE \
    -DANDROID_STL=c++_shared \
    -DANDROID_PLATFORM=21 \
    -DANDROID_USE_LEGACY_TOOLCHAIN_FILE=OFF \
    -DNDK_CCACHE=/usr/bin/ccache \
    -S "${CMAKE_DIR}" \
    -B "${BUILD_DIR}" \
    -DZSTD_MULTITHREAD_SUPPORT=ON \
    -DZSTD_BUILD_TESTS=OFF \
    -DZSTD_BUILD_CONTRIB=OFF \
    -DZSTD_BUILD_PROGRAMS=ON \
    -DZSTD_BUILD_STATIC=ON \
    -DZSTD_BUILD_SHARED=ON \
    -DZSTD_ZLIB_SUPPORT=ON \
    -DZSTD_LZMA_SUPPORT=ON \
    -DZSTD_LZ4_SUPPORT=OFF

  /usr/bin/cmake --build "${BUILD_DIR}" -- -j8
  /usr/bin/cmake --install "${BUILD_DIR}"

  # cleanup
  unset BUILD_DIR
  unset PACKAGE_INCLUDES
}

__clean
__install_required_packages
__install_android_ndk
__find_abi_kind
__install_lib_zlib
__install_lib_xz
__install_zstd
