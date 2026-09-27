#!/usr/bin/env bash
# bundle-libs.sh LIBDIR BIN...  -- copy every shared lib the binaries need into LIBDIR, except
# glibc/libstdc++/libgcc (hfbox has the same Ubuntu 24.04 ones) and /workspace/rocm (shipped
# separately), then point the binaries and the copied libs at it via an $ORIGIN rpath.
set -euo pipefail
LIBDIR=$1; shift; mkdir -p "$LIBDIR"
SKIP='^(linux-vdso|ld-linux|libc|libm|libdl|librt|libpthread|libstdc\+\+|libgcc_s)\.so'
for bin in "$@"; do
  ldd "$bin" | awk '/=> \//{print $1, $3}' | while read -r name path; do
    [[ $name =~ $SKIP || $path == /workspace/rocm/* || $path == "$LIBDIR"/* ]] && continue
    [ -e "$LIBDIR/$name" ] || cp -L "$path" "$LIBDIR/$name"
  done
done
for lib in "$LIBDIR"/*.so*; do patchelf --set-rpath '$ORIGIN:/workspace/rocm/lib' "$lib"; done
for bin in "$@"; do patchelf --set-rpath '$ORIGIN/../lib:/workspace/rocm/lib' "$bin"; done
echo "bundled $(ls "$LIBDIR" | wc -l) libs, $(du -sh "$LIBDIR" | cut -f1) -> $LIBDIR"
