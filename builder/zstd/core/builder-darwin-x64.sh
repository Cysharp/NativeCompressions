#!/bin/bash
set -eu

cd $SRC_DIR
  make clean
  make CFLAGS="-target x86_64-apple-macos10.12 -Werror -O3" lib # github action alrady include x86_64 LZ4/LZMA and failed on arm64
cd ..
