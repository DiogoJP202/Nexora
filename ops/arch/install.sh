#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
export PATH=/usr/bin:/bin LC_ALL=C
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$script_dir/common.sh"

usage() {
    printf '%s\n' 'Usage: install.sh --bundle /absolute/extracted/bundle --release-id ID --manifest-sha256 HASH' \
        'Installs one immutable release and missing directories/units. Never activates, starts, enables or migrates.'
}
bundle='' release_id='' manifest_hash=''
while (($#)); do
    case $1 in
        --bundle|--release-id|--manifest-sha256)
            (($# >= 2)) || nx_die 'missing argument value'
            case $1 in --bundle) bundle=$2;; --release-id) release_id=$2;; --manifest-sha256) manifest_hash=${2,,};; esac
            shift 2;;
        --help|-h) usage; exit 0;;
        *) usage >&2; nx_die 'unknown argument';;
    esac
done
[[ -n $bundle && -n $release_id && $manifest_hash =~ ^[a-f0-9]{64}$ ]] || { usage >&2; nx_die 'all three arguments are required'; }
nx_require_root
for cmd in getent id stat find sha256sum cp chmod chown mkdir mktemp mv install cmp python3 passwd groupadd useradd flock rm; do nx_require_command "$cmd"; done
[[ $(uname -s) == Linux && $(uname -m) == x86_64 && -f /etc/arch-release ]] || nx_die 'target must be an Arch Linux x86_64 host'
nx_assert_release_id "$release_id"
nx_assert_plain_path "$bundle"
[[ -f $bundle/SHA256SUMS ]] || nx_die 'bundle manifest is missing'
actual_manifest_hash=$(sha256sum -- "$bundle/SHA256SUMS")
[[ ${actual_manifest_hash:0:64} == "$manifest_hash" ]] || nx_die 'SHA256SUMS differs from the trusted origin hash'
nx_verify_sha256_manifest "$bundle"
[[ $(<"$bundle/release-id.txt") == "$release_id" ]] || nx_die 'release-id does not match the bundle'
python3 - "$bundle/release.json" "$release_id" <<'PY'
import json, re, sys
try:
    with open(sys.argv[1], encoding="utf-8") as source:
        release = json.load(source)
    valid = (release.get("schemaVersion") == 1 and release.get("releaseId") == sys.argv[2]
             and release.get("runtimeIdentifier") == "linux-x64"
             and release.get("sourceDirty") is False
             and re.fullmatch(r"[0-9a-fA-F]{40}", release.get("sourceCommit", ""))
             and isinstance(release.get("sdkVersion"), str) and release["sdkVersion"].startswith("10.")
             and isinstance(release.get("createdAtUtc"), str)
             and isinstance(release.get("lastMigration"), str))
    if not valid:
        raise ValueError()
except (OSError, ValueError, TypeError, AttributeError):
    sys.exit("Nexora: release.json metadata does not match the supported bundle contract")
PY
while IFS= read -r -d '' entry; do
    relative=${entry#"$bundle/"}
    nx_assert_relative_path "$relative"
    case $relative in
        api|api/*|worker|worker/*|migrations|migrations/*|ops|ops/arch|ops/arch/*|release-id.txt|release.json|SHA256SUMS) ;;
        *) nx_die 'unexpected entry outside the bundle layout';;
    esac
done < <(find "$bundle" -mindepth 1 -print0)
for entry in api/Nexora.Api worker/Nexora.Worker migrations/efbundle ops/arch/systemd/nexora-api.service ops/arch/systemd/nexora-worker.service; do
    [[ -f $bundle/$entry ]] || nx_die 'bundle is missing a required executable or unit'
done

# Refuse unmanaged ancestors, including symlinked mount paths. Dedicated mounts
# on these real directories are supported; moving storage is an operator task.
for path in /opt /etc /etc/systemd /etc/systemd/system /var /var/lib /srv; do
    nx_assert_plain_path "$path"
    [[ -d $path && $(stat -c '%u' -- "$path") == 0 ]] || nx_die 'a managed path ancestor must be a root-owned directory'
    path_mode=$(stat -c '%a' -- "$path")
    (( (8#$path_mode & 0022) == 0 )) || nx_die 'a managed path ancestor is writable by group or others'
done
for path in /opt/nexora /opt/nexora/releases /etc/nexora /var/lib/nexora /var/lib/nexora/keys /srv/nexora /srv/nexora/blobs /srv/nexora/temp /srv/nexora/thumbnails /srv/nexora/previews; do
    nx_assert_plain_path "$path"
    [[ ! -e $path || -d $path ]] || nx_die 'a managed directory path is occupied by another file type'
done
destination="/opt/nexora/releases/$release_id"
[[ ! -e $destination && ! -L $destination ]] || nx_die 'release already exists; overwrite is forbidden'
for unit in nexora-api.service nexora-worker.service; do
    target="/etc/systemd/system/$unit"
    nx_assert_plain_path "$target"
    if [[ -e $target ]]; then
        [[ -f $target && $(stat -c '%U:%G:%a:%h' -- "$target") == root:root:644:1 ]] || nx_die 'existing unit has unsafe ownership, mode or link count'
        cmp -s -- "$bundle/ops/arch/systemd/$unit" "$target" || nx_die 'existing unit differs; review and replace it manually before installation'
    fi
done
[[ -x /usr/bin/nologin ]] || nx_die '/usr/bin/nologin is unavailable'
if ! getent group nexora >/dev/null; then groupadd --system nexora; fi
if ! getent passwd nexora >/dev/null; then
    useradd --system --gid nexora --home-dir /var/lib/nexora --no-create-home --shell /usr/bin/nologin nexora
fi
nx_assert_service_account

# Only newly created, explicitly named directories get ownership/mode changes.
# Existing data trees are checked and never recursively chowned or chmodded.
ensure_directory() {
    local path=$1 owner=$2 group=$3 mode=$4
    if [[ -d $path ]]; then nx_assert_owned_dir "$path" "$owner" "$group" "$mode";
    else install -d -o "$owner" -g "$group" -m "$mode" -- "$path"; fi
}
ensure_directory /opt/nexora root root 755
nx_assert_plain_path /opt/nexora/.deploy.lock
if [[ -e /opt/nexora/.deploy.lock ]]; then
    [[ -f /opt/nexora/.deploy.lock && $(stat -c '%U:%G:%a:%h' -- /opt/nexora/.deploy.lock) == root:root:600:1 ]] || nx_die 'unsafe deployment lock'
fi
exec 9>/opt/nexora/.deploy.lock
flock -n 9 || nx_die 'another installation or activation is running'
[[ ! -e $destination && ! -L $destination ]] || nx_die 'release already exists; overwrite is forbidden'
ensure_directory /opt/nexora/releases root root 755
ensure_directory /etc/nexora root nexora 750
ensure_directory /var/lib/nexora root root 755
ensure_directory /var/lib/nexora/keys nexora nexora 700
ensure_directory /srv/nexora nexora nexora 700
for name in blobs temp thumbnails previews; do ensure_directory "/srv/nexora/$name" nexora nexora 700; done
nx_assert_tree_plain /srv/nexora
nx_assert_tree_plain /var/lib/nexora/keys

stage=$(mktemp -d /opt/nexora/releases/.install-XXXXXXXX)
cleanup() {
    if [[ ${stage:-} == /opt/nexora/releases/.install-* && -d $stage && ! -L $stage ]]; then
        rm -rf -- "$stage"
    fi
}
trap cleanup EXIT
# Do not inherit source write permissions into the root-owned staging tree.
# With umask 077, unprivileged users cannot race validation/permission changes.
cp -r --preserve=timestamps --no-preserve=ownership,mode -- "$bundle/." "$stage/"
nx_verify_sha256_manifest "$stage"
copied_manifest_hash=$(sha256sum -- "$stage/SHA256SUMS")
[[ ${copied_manifest_hash:0:64} == "$manifest_hash" ]] || nx_die 'bundle changed during copy'
find "$stage" -type d -exec chown root:root -- {} + -exec chmod 0755 -- {} +
find "$stage" -type f -exec chown root:root -- {} + -exec chmod 0644 -- {} +
chmod 0755 -- "$stage/api/Nexora.Api" "$stage/worker/Nexora.Worker" "$stage/migrations/efbundle"
find "$stage/ops/arch" -type f -name '*.sh' -exec chmod 0755 -- {} +
[[ ! -e $destination && ! -L $destination ]] || nx_die 'release destination appeared during installation'
mv -T -- "$stage" "$destination"
stage=''
for unit in nexora-api.service nexora-worker.service; do
    target="/etc/systemd/system/$unit"
    [[ -f $target ]] || install -o root -g root -m 0644 -- "$destination/ops/arch/systemd/$unit" "$target"
done
printf 'Installed release %s. No current link, service state, PostgreSQL cluster or schema was changed.\n' "$release_id"
printf '%s\n' 'Configure /etc/nexora separately; review units, run systemctl daemon-reload, then verify and activate explicitly.'
