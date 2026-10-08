#!/usr/bin/env bash
# Shared guards; never source service EnvironmentFile content as shell code.

nx_die() { printf 'Nexora: %s\n' "$*" >&2; exit 1; }
nx_require_root() { [[ ${EUID:-$(id -u)} -eq 0 ]] || nx_die 'run this command as root'; }
nx_require_command() { command -v "$1" >/dev/null 2>&1 || nx_die "required command is unavailable: $1"; }

# Missing final components are allowed. Callers must test the expected file type.
nx_assert_plain_path() {
    local path=$1 component current=''
    [[ $path == /* && $path != / && $path != */ && $path != *//* && $path != *\\* ]] ||
        nx_die 'expected a canonical absolute path outside the filesystem root'
    [[ $path != *$'\n'* && $path != *$'\r'* ]] || nx_die 'invalid path characters'
    local -a components
    IFS=/ read -r -a components <<< "${path#/}"
    for component in "${components[@]}"; do
        [[ -n $component && $component != . && $component != .. ]] || nx_die 'path traversal is forbidden'
        current+="/$component"
        [[ ! -L $current ]] || nx_die 'symbolic links in managed path components are forbidden'
        [[ ! -e $current || -d $current || $current == "$path" ]] || nx_die 'a path ancestor is not a directory'
    done
}

nx_assert_tree_plain() {
    local path=$1 invalid
    nx_assert_plain_path "$path"
    [[ -d $path ]] || nx_die 'expected an existing directory'
    invalid=$(find "$path" \( \( ! -type d ! -type f \) -o \( -type f -links +1 \) \) -print -quit) ||
        nx_die 'unable to inspect directory tree'
    [[ -z $invalid ]] || nx_die 'non-regular entries or hardlinks are forbidden'
}

nx_assert_owned_dir() {
    local path=$1 owner=$2 group=$3 mode=$4
    nx_assert_plain_path "$path"
    [[ -d $path ]] || nx_die 'expected an existing managed directory'
    [[ $(stat -c '%U:%G:%a' -- "$path") == "$owner:$group:$mode" ]] ||
        nx_die 'managed directory ownership or mode differs from the required value; inspect it manually'
}

nx_assert_relative_path() {
    local path=$1 component
    [[ $path =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ && $path != */ && $path != *//* ]] ||
        nx_die 'manifest contains an invalid relative path'
    local -a components
    IFS=/ read -r -a components <<< "$path"
    for component in "${components[@]}"; do
        [[ $component != . && $component != .. ]] || nx_die 'manifest path traversal is forbidden'
    done
}

# SHA256SUMS must cover every regular file except itself. This checks integrity,
# not provenance; install.sh additionally requires its externally trusted hash.
nx_verify_sha256_manifest() {
    local root=$1 line relative expected actual file count=0
    nx_assert_tree_plain "$root"
    [[ -f $root/SHA256SUMS && $(stat -c '%h' -- "$root/SHA256SUMS") == 1 ]] ||
        nx_die 'SHA256SUMS is missing or linked'
    local -A hashes=()
    while IFS= read -r line || [[ -n $line ]]; do
        [[ $line =~ ^([a-fA-F0-9]{64})\ \ ([A-Za-z0-9][A-Za-z0-9._/-]*)$ ]] ||
            nx_die 'malformed SHA256SUMS entry (LF and two spaces are required)'
        expected=${BASH_REMATCH[1],,}; relative=${BASH_REMATCH[2]}
        nx_assert_relative_path "$relative"
        [[ $relative != SHA256SUMS && ! -v hashes["$relative"] ]] || nx_die 'duplicate or recursive manifest entry'
        file="$root/$relative"
        [[ -f $file && ! -L $file ]] || nx_die 'a manifest file is missing'
        actual=$(sha256sum -- "$file") || nx_die 'unable to hash a manifest file'
        [[ ${actual:0:64} == "$expected" ]] || nx_die 'manifest hash mismatch'
        hashes["$relative"]=$expected
    done < "$root/SHA256SUMS"
    [[ ${#hashes[@]} -gt 0 ]] || nx_die 'empty manifest'
    while IFS= read -r -d '' relative; do
        relative=${relative#"$root/"}
        [[ $relative == SHA256SUMS ]] && continue
        nx_assert_relative_path "$relative"
        [[ -v hashes["$relative"] ]] || nx_die 'bundle contains a file absent from SHA256SUMS'
        ((count+=1))
    done < <(find "$root" -type f -print0)
    [[ $count -eq ${#hashes[@]} ]] || nx_die 'manifest coverage mismatch'
}

nx_assert_release_id() {
    [[ $1 =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ && $1 != . && $1 != .. ]] || nx_die 'invalid release-id'
}

nx_assert_service_account() {
    local record name password uid gid gecos home_dir login_shell group_record account_status
    record=$(getent passwd nexora) || nx_die 'nexora system account is missing'
    IFS=: read -r name password uid gid gecos home_dir login_shell <<< "$record"
    [[ $uid -gt 0 && $uid -lt 1000 && $gid -gt 0 && $gid -lt 1000 && $home_dir == /var/lib/nexora && $login_shell == /usr/bin/nologin ]] ||
        nx_die 'existing nexora account does not match the fixed system account contract'
    group_record=$(getent group nexora) || nx_die 'nexora group is missing'
    [[ ${group_record%%:*} == nexora && $(id -gn nexora) == nexora && $(id -Gn nexora) == nexora ]] ||
        nx_die 'nexora must use only the nexora group'
    account_status=$(passwd -S nexora) || nx_die 'unable to inspect account lock state'
    [[ $account_status =~ ^nexora[[:space:]]+L[[:space:]] ]] || nx_die 'nexora account password must remain locked'
}
