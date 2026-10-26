#!/bin/sh
# Run one Cargo command against the FreeBSD-patched llama.cpp dependency.
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
lock="$root/Cargo.lock"
backup=$(mktemp "$root/Cargo.lock.XXXXXX")
cp "$lock" "$backup"
restore_lock() {
    cp "$backup" "$lock"
    rm -f "$backup"
}
trap restore_lock EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

config=$(sh "$root/packaging/llama/patch-llama.sh")
set -- "$@" --locked --manifest-path "$root/Cargo.toml"
[ -z "$config" ] || set -- "$@" --config "$config"
cargo "$@"
