#!/usr/bin/env bash
# Filesystem-only checks: no root, systemd, PostgreSQL or installed host needed.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$script_dir/../../ops/arch/common.sh"
work=$(mktemp -d "${TMPDIR:-/tmp}/nexora-guards.XXXXXXXX")
passed=0 skipped=0
cleanup() {
    # Delete only explicitly named fixture files; never enumerate another tree
    # or recursively delete a path derived from arbitrary test input.
    local name
    for name in original-manifest bundle/payload.txt bundle/SHA256SUMS bundle/extra.txt bundle/hardlink.txt bundle/symbolic.txt; do
        [[ ! -e $work/$name && ! -L $work/$name ]] || rm -f -- "$work/$name"
    done
    if [[ -L $work/linked-bundle ]]; then rm -- "$work/linked-bundle";
    elif [[ -d $work/linked-bundle ]]; then
        rm -f -- "$work/linked-bundle/payload.txt" "$work/linked-bundle/SHA256SUMS"
        rmdir -- "$work/linked-bundle"
    fi
    [[ ! -d $work/bundle ]] || rmdir -- "$work/bundle"
    rmdir -- "$work"
}
trap cleanup EXIT
pass() { printf 'PASS %s\n' "$1"; ((passed+=1)); }
reject() {
    local label=$1; shift
    if ( "$@" ) >/dev/null 2>&1; then printf 'FAIL %s accepted invalid input\n' "$label" >&2; exit 1; fi
    pass "$label"
}
mkdir "$work/bundle"
printf '%s\n' 'safe payload' > "$work/bundle/payload.txt"
(cd "$work/bundle" && sha256sum --text payload.txt > SHA256SUMS)
nx_verify_sha256_manifest "$work/bundle"
pass 'complete valid manifest'
printf '%s\n' 'tampered' > "$work/bundle/payload.txt"
reject 'tampered payload' nx_verify_sha256_manifest "$work/bundle"
printf '%s\n' 'safe payload' > "$work/bundle/payload.txt"
printf '%s\n' 'extra' > "$work/bundle/extra.txt"
reject 'file absent from manifest' nx_verify_sha256_manifest "$work/bundle"
rm -- "$work/bundle/extra.txt"
cp "$work/bundle/SHA256SUMS" "$work/original-manifest"
cat "$work/original-manifest" >> "$work/bundle/SHA256SUMS"
reject 'duplicated manifest entry' nx_verify_sha256_manifest "$work/bundle"
cp "$work/original-manifest" "$work/bundle/SHA256SUMS"
printf '%064d  ../payload.txt\n' 0 > "$work/bundle/SHA256SUMS"
reject 'traversal in manifest' nx_verify_sha256_manifest "$work/bundle"
cp "$work/original-manifest" "$work/bundle/SHA256SUMS"
printf '%064d  missing.txt\n' 0 > "$work/bundle/SHA256SUMS"
reject 'missing manifest file' nx_verify_sha256_manifest "$work/bundle"
cp "$work/original-manifest" "$work/bundle/SHA256SUMS"
ln -- "$work/bundle/payload.txt" "$work/bundle/hardlink.txt"
reject 'hardlink in data tree' nx_assert_tree_plain "$work/bundle"
rm -- "$work/bundle/hardlink.txt"
ln -s -- "$work/bundle/payload.txt" "$work/bundle/symbolic.txt"
if [[ -L $work/bundle/symbolic.txt ]]; then
    reject 'symlink in data tree' nx_assert_tree_plain "$work/bundle"
else
    printf '%s\n' 'SKIP real file symlink: this Bash environment emulates links as copies'
    ((skipped+=1))
fi
rm -- "$work/bundle/symbolic.txt"
ln -s -- "$work/bundle" "$work/linked-bundle"
if [[ -L $work/linked-bundle ]]; then
    reject 'symlink as managed root' nx_assert_plain_path "$work/linked-bundle"
    reject 'symlink in managed ancestors' nx_assert_plain_path "$work/linked-bundle/new"
else
    printf '%s\n' 'SKIP real directory symlinks: this Bash environment emulates links as copies'
    ((skipped+=2))
fi
reject 'parent component' nx_assert_plain_path "$work/bundle/../outside"
reject 'noncanonical double slash' nx_assert_plain_path "$work//bundle"
reject 'non-absolute path' nx_assert_plain_path bundle
reject 'relative manifest traversal' nx_assert_relative_path '../payload.txt'
reject 'nested manifest traversal' nx_assert_relative_path 'api/../payload.txt'
reject 'backslash in manifest path' nx_assert_relative_path 'api\payload.txt'
reject 'absolute manifest path' nx_assert_relative_path '/payload.txt'
nx_assert_release_id '20261008T120000Z-abcdef12'
pass 'valid release ID'
reject 'release ID traversal' nx_assert_release_id '../escape'
reject 'release ID slash' nx_assert_release_id 'one/two'
reject 'release ID exceeds 64 characters' nx_assert_release_id 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
printf 'Guard checks: %s passed, %s skipped. No host services or database were used.\n' "$passed" "$skipped"
