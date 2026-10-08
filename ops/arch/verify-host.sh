#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
export PATH=/usr/bin:/bin LC_ALL=C
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$script_dir/common.sh"

usage() { printf '%s\n' 'Usage: verify-host.sh [--release-id ID]' 'Read-only prerequisite checks. An omitted ID checks the root-managed current link.'; }
release_id=''
while (($#)); do
    case $1 in
        --release-id) (($# >= 2)) || nx_die 'missing release-id'; release_id=$2; shift 2;;
        --help|-h) usage; exit 0;;
        *) usage >&2; nx_die 'unknown argument';;
    esac
done
nx_require_root
for cmd in stat find sha256sum getent passwd id readlink python3 pacman systemctl systemd-analyze runuser env psql grep file cmp; do nx_require_command "$cmd"; done
[[ $(uname -s) == Linux && $(uname -m) == x86_64 && -f /etc/arch-release ]] || nx_die 'target must be Arch Linux x86_64'
[[ -r /sys/fs/cgroup/cgroup.controllers ]] || nx_die 'cgroup v2 is required'
grep -qw memory /sys/fs/cgroup/cgroup.controllers || nx_die 'cgroup v2 memory controller is unavailable'
nx_assert_service_account
for path in /opt /etc /etc/systemd /etc/systemd/system /var /var/lib /srv; do
    nx_assert_plain_path "$path"
    [[ -d $path && $(stat -c '%u' -- "$path") == 0 ]] || nx_die 'managed path ancestor is not root-owned'
    mode=$(stat -c '%a' -- "$path")
    (( (8#$mode & 0022) == 0 )) || nx_die 'managed path ancestor is writable by group or others'
done
nx_assert_owned_dir /opt/nexora root root 755
nx_assert_owned_dir /opt/nexora/releases root root 755
nx_assert_owned_dir /etc/nexora root nexora 750
nx_assert_owned_dir /var/lib/nexora root root 755
nx_assert_owned_dir /var/lib/nexora/keys nexora nexora 700
nx_assert_owned_dir /srv/nexora nexora nexora 700
for name in blobs temp thumbnails previews; do nx_assert_owned_dir "/srv/nexora/$name" nexora nexora 700; done
nx_assert_tree_plain /srv/nexora
nx_assert_tree_plain /var/lib/nexora/keys
nx_assert_tree_plain /etc/nexora
for name in common.env api.env worker.env; do
    [[ -f /etc/nexora/$name && $(stat -c '%U:%G:%a:%h' -- "/etc/nexora/$name") == root:root:600:1 ]] ||
        nx_die 'each service EnvironmentFile must be a regular root:root 0600 file with one link'
done
[[ -f /etc/nexora/data-protection.pfx && $(stat -c '%U:%G:%a:%h' -- /etc/nexora/data-protection.pfx) == root:nexora:640:1 ]] ||
    nx_die 'Data Protection PFX must be a regular root:nexora 0640 file with one link'

# Restrict deployment env files to single-line assignments. This is a parser,
# never eval/source; errors never print file values or connection strings.
python3 - <<'PY'
import pathlib, re, sys
def fail(message):
    sys.exit("Nexora: " + message)
def read_env(name):
    values = {}
    try:
        text = pathlib.Path("/etc/nexora", name).read_text(encoding="utf-8")
        if "\ufeff" in text or "\0" in text:
            fail("EnvironmentFiles must be UTF-8 without BOM/NUL")
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith(("#", ";")):
                continue
            match = re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)=(.*)", line)
            if not match or match[1] in values:
                fail("invalid or duplicated EnvironmentFile assignment")
            value = match[2].strip()
            if value.startswith(("'", '"')):
                if len(value) < 2 or value[-1] != value[0] or value[0] in value[1:-1]:
                    fail("unsupported EnvironmentFile quoting; use a single-line value")
                value = value[1:-1]
            if "\\" in value or any(ord(c) < 32 for c in value):
                fail("unsupported EnvironmentFile escapes or control characters")
            values[match[1]] = value
    except (OSError, UnicodeError):
        fail("unable to read EnvironmentFiles")
    return values
common, api, worker = (read_env(name) for name in ("common.env", "api.env", "worker.env"))
for values in (common | api, common | worker):
    if values.get("NEXORA_Storage__RootPath") != "/srv/nexora":
        fail("storage path must match the systemd writable directory")
    if not values.get("NEXORA_ConnectionStrings__Nexora", "").strip():
        fail("the shared PostgreSQL connection is required")
    for key in ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "NEXORA_Environment"):
        if key in values and values[key] != "Production":
            fail("service environment overrides must remain Production")
    for key in ("ASPNETCORE_URLS", "DOTNET_URLS", "NEXORA_Urls", "URLS"):
        if key in values and values[key] != "http://127.0.0.1:5100":
            fail("API URL overrides must remain loopback port 5100")
    if any(key.startswith(("NEXORA_Kestrel__", "Kestrel__")) for key in values):
        fail("custom Kestrel endpoints require a separately reviewed deployment")
    if "DOTNET_GCHeapHardLimit" in values and values["DOTNET_GCHeapHardLimit"].lower() != "10000000":
        fail("parent managed heap override differs from the reviewed 256 MiB budget")
if common.get("NEXORA_ConnectionStrings__Nexora") != (common | api).get("NEXORA_ConnectionStrings__Nexora") or common.get("NEXORA_ConnectionStrings__Nexora") != (common | worker).get("NEXORA_ConnectionStrings__Nexora"):
    fail("API and Worker must share the same connection from common.env")
if (common | api).get("NEXORA_DataProtection__KeyDirectory") != "/var/lib/nexora/keys" or (common | api).get("NEXORA_DataProtection__CertificatePath") != "/etc/nexora/data-protection.pfx":
    fail("Data Protection paths must match the fixed deployment contract")
hosts = api.get("NEXORA_AllowedHosts", "").split(";")
if not hosts or any(not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", host) for host in hosts):
    fail("api.env requires explicit AllowedHosts without wildcards or ports")
if not any(host.endswith(".ts.net") for host in hosts):
    fail("AllowedHosts must include the actual Tailscale HTTPS FQDN")
try:
    child_budget = int((common | worker).get("NEXORA_Images__MaximumProcessMemoryBytes", "536870912"))
except ValueError:
    fail("invalid image child budget")
if child_budget != 536870912:
    fail("image child budget differs from the reviewed 512 MiB limit")
PY

if [[ -z $release_id ]]; then
    [[ -L /opt/nexora/current && $(stat -c '%U:%G' -- /opt/nexora/current) == root:root ]] || nx_die 'current must be a root-managed symlink; pass --release-id for the initial install'
    current_target=$(readlink -- /opt/nexora/current)
    [[ $current_target == /opt/nexora/releases/* ]] || nx_die 'current target is outside the release layout'
    release_id=${current_target#/opt/nexora/releases/}
fi
nx_assert_release_id "$release_id"
release="/opt/nexora/releases/$release_id"
nx_assert_owned_dir "$release" root root 755
nx_verify_sha256_manifest "$release"
[[ $(<"$release/release-id.txt") == "$release_id" ]] || nx_die 'installed release-id mismatch'
invalid=$(find "$release" \( ! -user root -o ! -group root -o -perm /0022 \) -print -quit)
[[ -z $invalid ]] || nx_die 'release has writable or non-root-owned content'
for executable in api/Nexora.Api worker/Nexora.Worker migrations/efbundle; do
    [[ -f $release/$executable && -x $release/$executable ]] || nx_die 'release executable is missing or not executable'
    file_description=$(file -b -- "$release/$executable")
    [[ $file_description == *'ELF 64-bit'* && $file_description == *'x86-64'* ]] || nx_die 'release executable is not Linux x86_64 ELF'
done
for unit in nexora-api.service nexora-worker.service; do
    nx_assert_plain_path "/etc/systemd/system/$unit"
    [[ $(stat -c '%U:%G:%a:%h' -- "/etc/systemd/system/$unit") == root:root:644:1 ]] || nx_die 'unsafe systemd unit ownership or mode'
    cmp -s -- "$release/ops/arch/systemd/$unit" "/etc/systemd/system/$unit" || nx_die 'installed unit differs from this release; review it manually'
    [[ -z $(systemctl show --value --property=DropInPaths "$unit") ]] || nx_die 'systemd unit has unreviewed drop-ins; inspect effective paths and limits manually'
done
if [[ -L /opt/nexora/current ]]; then
    systemd-analyze verify /etc/systemd/system/nexora-api.service /etc/systemd/system/nexora-worker.service
else
    printf '%s\n' 'Unit load verification is deferred until the initial current link exists; activation repeats it before starting services.'
fi
for package in glibc icu krb5 libgcc libstdc++ libunwind openssl zlib postgresql tailscale; do
    pacman -Q "$package" >/dev/null || nx_die "required host package is missing: $package"
done
server_version=$(runuser -u postgres -- env -i PATH=/usr/bin:/bin LC_ALL=C PGPASSFILE=/dev/null PGSERVICEFILE=/dev/null \
    psql -X -A -t --no-password --host=/run/postgresql --port=5432 --username=postgres --dbname=postgres --command='SHOW server_version_num' 2>/dev/null) || nx_die 'cannot inspect the running local PostgreSQL server with peer authentication'
[[ $server_version =~ ^18[0-9]{4}$ ]] || nx_die 'the running PostgreSQL server must be major version 18'
printf 'Prerequisite checks passed for release %s. No services, files or database schema were changed.\n' "$release_id"
printf '%s\n' 'Still verify the PFX RSA private key, native image processing, HTTPS/Tailnet grants, Funnel disabled, migrations and readiness on the real host.'
