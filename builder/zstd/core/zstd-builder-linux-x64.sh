#!/bin/sh
apk --no-cache add make gcc libc-dev
cd /src
make clean
make
