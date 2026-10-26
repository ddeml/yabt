#!/usr/bin/env bash
set -euo pipefail

[[ $(id -u) == 0 ]] || { echo 'Workspace initialization requires container root.' >&2; exit 1; }
mountpoint -q /work || { echo '/work must be the dedicated workspace volume.' >&2; exit 1; }
for directory in checkouts runs tmp state nuget cli-home imports home; do
    path="/work/$directory"
    [[ ! -L "$path" ]] || { echo "Refusing linked workspace directory: $path" >&2; exit 1; }
    install -d -m 0755 -o 10001 -g 10001 "$path"
done
