#!/bin/sh
apt-get update
apt-get install -yq --no-install-suggests --no-install-recommends make gcc libc-dev sudo
cd /src
make clean
make arminstall
make aarch64build
