#!/bin/sh
set -eu

# install
apt-get update
apt-get install -yq --no-install-suggests --no-install-recommends make gcc libc-dev
apt-get install -yq --no-install-suggests --no-install-recommends gcc-multilib
apt-get install -yq --no-install-suggests --no-install-recommends qemu-utils qemu-user-static
apt-get install -yq --no-install-suggests --no-install-recommends qemu-system-arm gcc-aarch64-linux-gnu g++-aarch64-linux-gnu

# setup toolchains
XCC=aarch64-linux-gnu-gcc
XEMU=qemu-aarch64-static

echo && type $XCC && which $XCC && $XCC --version
echo && $XCC -v
echo && type $XEMU && which $XEMU && $XEMU --version

# build
cd /src
make clean
make CC=$XCC QEMU_SYS=$XEMU
