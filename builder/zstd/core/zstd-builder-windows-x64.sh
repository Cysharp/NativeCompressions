#!/bin/sh
# see: https://github.com/facebook/zstd/issues/2072
apt-get update
apt-get install -yq --no-install-suggests --no-install-recommends make gcc libc-dev sudo mingw-w64
cd /src
make clean
make EXT=.exe TARGET_SYSTEM=Windows CC=x86_64-w64-mingw32-gcc WINDRES=x86_64-w64-mingw32-windres AR=x86_64-w64-mingw32-ar
