#!/bin/sh
# Usage: sh .github/scripts/check-symbol-binding.sh <library.so>...
#
# A Linux process can already hold another copy of lz4 or zstd, e.g. the distro library linked by the host
# executable. The dynamic loader resolves a library's references to its own exported functions through the
# process's global scope first. So without -Bsymbolic, the bundled library can call into that other copy with
# its own structs, and corrupt memory. This check fails when one of these is true for a library:
#   - it is not linked with -Bsymbolic (no DT_SYMBOLIC),
#   - a dynamic relocation still names a symbol that the library defines itself,
#   - it leaves a symbol of the lz4/zstd/OpenZL namespaces undefined, such as zstd's weak ZSTD_trace_* hooks.
#     -Bsymbolic does not bind undefined symbols, so build zstd with -DZSTD_TRACE=0.
set -eu

namespaces='^(ZL|ZS2|ZSTD|ZDICT|LZ4|LZ4F|XXH|HUF|FSE)_'
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT
status=0

for lib in "$@"; do
  failed=0

  if ! readelf -d "${lib}" | grep -q '(SYMBOLIC)'; then
    echo "::error::${lib} is not linked with -Wl,-Bsymbolic"
    failed=1
  fi

  nm -D --defined-only "${lib}" | awk '{ print $3 }' | sed 's/@.*//' | sort -u > "${tmp}/defined"
  # Relocation lines start with a hex offset and give the symbol name in the fifth field. RELATIVE relocations have no symbol.
  readelf -rW "${lib}" | awk '$1 ~ /^[0-9a-f]+$/ && NF >= 5 { print $5 }' | sed 's/@.*//' | sort -u > "${tmp}/relocated"
  comm -12 "${tmp}/defined" "${tmp}/relocated" > "${tmp}/self"
  if [ -s "${tmp}/self" ]; then
    echo "::error::${lib} resolves its own symbols at load time, another copy in the process can take them: $(tr '\n' ' ' < "${tmp}/self")"
    failed=1
  fi

  nm -D --undefined-only "${lib}" | awk '{ print $2 }' | sed 's/@.*//' | grep -E "${namespaces}" > "${tmp}/undefined" || true
  if [ -s "${tmp}/undefined" ]; then
    echo "::error::${lib} leaves lz4/zstd/OpenZL symbols undefined, another library in the process can define them: $(tr '\n' ' ' < "${tmp}/undefined")"
    failed=1
  fi

  if [ "${failed}" -eq 0 ]; then
    echo "${lib}: binds all of its own symbols to itself"
  else
    status=1
  fi
done

exit "${status}"
