#!/bin/sh
# use https://github.com/mstorsjo/llvm-mingw for ARM64 support.
# see: https://github.com/facebook/zstd/issues/2072
apt-get update
apt-get install -yq --no-install-suggests --no-install-recommends make gcc libc-dev
cd /src
make clean
make EXT=.exe TARGET_SYSTEM=Windows CC=aarch64-w64-mingw32-gcc WINDRES=aarch64-w64-mingw32-windres AR=aarch64-w64-mingw32-ar
