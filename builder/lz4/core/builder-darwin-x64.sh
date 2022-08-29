#!/bin/bash
set -eu

cd $SRC_DIR
  make clean
  make CFLAGS="-target x86_64-apple-macos10.12 -Werror -O3"
cd ..
