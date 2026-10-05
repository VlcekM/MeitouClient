#!/usr/bin/env bash
# Compare two parity.sh output folders: parity-compare.sh <meitou-tools exe> <dir a> <dir b>
set -u
tools="$1"; a="$2"; b="$3"
for f in "$a"/*.png; do
  n=$(basename "$f")
  case "$n" in *.diff.png) continue;; esac
  printf '%-20s ' "$n"
  "$tools" image-diff "$f" "$b/$n" "$b/${n%.png}.diff.png"
done
