#!/usr/bin/env bash
# Restore rehearsal only. Never replace production data or apply archived envs.
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin LC_ALL=C
unset TAR_OPTIONS GZIP
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

usage() {
    printf '%s\n' 'Usage: sudo bash restore.sh --isolated --backup /mnt/nexora-backups/<snapshot>' \
        '  --release-dir /opt/nexora/releases/<release-id>' \
        '  --database nexora_restore_<id> --target-root /var/lib/nexora-restore/<id>' \
        'Requires a new target directory and a nonexistent database. Starts no service.'
}
isolated=no; backup=''; release_dir=''; database=''; target_root=''
while (($#)); do
    case $1 in
        --isolated) isolated=yes; shift ;;
        --backup|--release-dir|--database|--target-root)
            (($# >= 2)) || nx_die 'missing argument value'
            case $1 in
                --backup) backup=$2 ;;
                --release-dir) release_dir=$2 ;;
                --database) database=$2 ;;
                --target-root) target_root=$2 ;;
            esac
            shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) usage >&2; nx_die 'unknown argument' ;;
    esac
done
[[ $isolated == yes && -n $backup && -n $release_dir && -n $database && -n $target_root ]] ||
    { usage >&2; nx_die 'all arguments and the explicit --isolated acknowledgement are required'; }
nx_require_root
for command in systemctl runuser env psql pg_restore createdb tar gzip jq find stat sha256sum sort awk cmp cp mktemp rm grep tail cut cat mkdir chmod flock; do
    nx_require_command "$command"
done
[[ $database =~ ^nexora_restore_[a-z0-9_]{1,47}$ ]] || nx_die 'database must use the dedicated nexora_restore_ prefix and be at most 62 characters'
[[ $target_root =~ ^/var/lib/nexora-restore/[a-z0-9][a-z0-9_-]{0,62}$ ]] || nx_die 'target must be one literal child of /var/lib/nexora-restore'
nx_assert_owned_dir /var/lib/nexora-restore root root 700
assert_root_ancestors() {
    local parent=$1 permissions
    while [[ $parent != / ]]; do
        [[ $(stat -c '%u' -- "$parent") == 0 ]] || nx_die 'restore and backup ancestors must be owned by root'
        permissions=$(stat -c '%a' -- "$parent")
        (( (8#$permissions & 0022) == 0 )) || nx_die 'restore and backup ancestors must not be writable by group or others'
        parent=${parent%/*}; [[ -n $parent ]] || parent=/
    done
}
assert_root_ancestors /var/lib/nexora-restore
nx_assert_plain_path "$target_root"
[[ ! -e $target_root && ! -L $target_root ]] || nx_die 'restore target already exists; use a new rehearsal identifier'
nx_assert_owned_dir "$backup" root root 700
[[ $backup =~ ^/[A-Za-z0-9_./-]+$ ]] || nx_die 'backup source must use portable ASCII path components'
assert_root_ancestors "$backup"
nx_assert_owned_dir /opt/nexora root root 755
nx_assert_owned_dir /opt/nexora/releases root root 755
nx_assert_plain_path /opt/nexora/.deploy.lock
if [[ -e /opt/nexora/.deploy.lock ]]; then
    [[ -f /opt/nexora/.deploy.lock && $(stat -c '%U:%G:%a:%h' -- /opt/nexora/.deploy.lock) == root:root:600:1 ]] || nx_die 'unsafe deployment lock'
fi
exec 9>/opt/nexora/.deploy.lock
flock -n 9 || nx_die 'another installation, activation, backup or restoration is running'
case $backup in
    /opt/nexora|/opt/nexora/*|/etc/nexora|/etc/nexora/*|/var/lib/nexora|/var/lib/nexora/*|/srv/nexora|/srv/nexora/*|/var/lib/nexora-restore|/var/lib/nexora-restore/*)
        nx_die 'backup source overlaps a managed Nexora tree' ;;
esac
[[ ${backup,,} != *'/nextcloud/'* && ${backup,,} != */nextcloud && $backup != *.incomplete ]] || nx_die 'use a completed snapshot outside Nextcloud'
nx_verify_sha256_manifest "$backup"
while IFS= read -r -d '' entry; do
    [[ -f $entry ]] || nx_die 'snapshot must contain only top-level regular files'
    case ${entry#"$backup/"} in
        SHA256SUMS|backup.log|database-settings.txt|database.dump|manifest.txt|migrations.txt|payload.tar.gz|release-id.txt|release.json|release-SHA256SUMS) ;;
        *) nx_die 'unexpected snapshot file' ;;
    esac
done < <(find "$backup" -mindepth 1 -print0)
for required in backup.log database-settings.txt database.dump manifest.txt migrations.txt payload.tar.gz release-id.txt release.json release-SHA256SUMS; do
    [[ -f $backup/$required ]] || nx_die 'snapshot is incomplete'
done

declare -A manifest=()
while IFS='=' read -r key value || [[ -n $key || -n $value ]]; do
    case $key in
        format|created_utc|release_id|database|database_owner|postgres_major|storage_root|keys_root|config_root|last_migration|api_was_active|worker_was_active) ;;
        *) nx_die 'unexpected snapshot manifest key' ;;
    esac
    [[ ! -v manifest["$key"] && -n $value ]] || nx_die 'duplicate or empty snapshot manifest value'
    manifest[$key]=$value
done < "$backup/manifest.txt"
[[ ${#manifest[@]} == 12 && ${manifest[format]} == nexora-backup-v1 && ${manifest[database]} == nexora && \
    ${manifest[database_owner]} == nexora_migrator && ${manifest[postgres_major]} == 18 && ${manifest[storage_root]} == /srv/nexora && \
    ${manifest[keys_root]} == /var/lib/nexora/keys && ${manifest[config_root]} == /etc/nexora ]] || nx_die 'unsupported snapshot contract'
[[ ${manifest[created_utc]} =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ && \
    ${manifest[api_was_active]} =~ ^(yes|no)$ && ${manifest[worker_was_active]} =~ ^(yes|no)$ ]] || nx_die 'malformed snapshot metadata'
release_id=${manifest[release_id]}
nx_assert_release_id "$release_id"
[[ $release_dir == "/opt/nexora/releases/$release_id" ]] || nx_die 'restore requires the exact snapshot release in the release tree'
nx_assert_owned_dir "$release_dir" root root 755
nx_verify_sha256_manifest "$release_dir"
[[ $(cat -- "$backup/release-id.txt") == "$release_id" && $(cat -- "$release_dir/release-id.txt") == "$release_id" ]] || nx_die 'release identifiers differ'
cmp -s -- "$backup/release.json" "$release_dir/release.json" || nx_die 'release metadata differs from the snapshot'
cmp -s -- "$backup/release-SHA256SUMS" "$release_dir/SHA256SUMS" || nx_die 'release contents differ from the snapshot'
jq -e --arg id "$release_id" '.schemaVersion == 1 and .releaseId == $id and .runtimeIdentifier == "linux-x64"' \
    "$release_dir/release.json" >/dev/null || nx_die 'unsupported release manifest'
[[ ${manifest[last_migration]} =~ ^[0-9]{14}_[A-Za-z0-9_]+$ && \
    $(jq -er '.lastMigration' "$release_dir/release.json") == "${manifest[last_migration]}" && \
    $(tail -n 1 "$backup/migrations.txt" | cut -f 1) == "${manifest[last_migration]}" ]] || nx_die 'migration metadata differs from the release'
[[ $(pg_restore --version) =~ \(PostgreSQL\)\ 18\. ]] || nx_die 'pg_restore 18 is required'

# Rehearsal requires an isolated host. A stopped production host is not a safe
# substitute; --isolated is the operator's explicit assertion about the host.
for unit in nexora-api.service nexora-worker.service; do
    load=$(systemctl show --property=LoadState --value "$unit")
    [[ $load == not-found ]] && continue
    [[ $load == loaded ]] || nx_die 'unable to inspect Nexora unit state'
    state=$(systemctl show --property=ActiveState --value "$unit")
    [[ $state == inactive || $state == failed ]] || nx_die 'Nexora services must be inactive on the isolated host'
    [[ $(systemctl show --property=MainPID --value "$unit") == 0 && $(systemctl show --property=ControlPID --value "$unit") == 0 ]] || nx_die 'a Nexora process is still running'
    group=$(systemctl show --property=ControlGroup --value "$unit")
    if [[ -n $group ]]; then
        [[ $group =~ ^/[A-Za-z0-9_.@/-]+$ && $group != *'/../'* && $group != *'/./'* ]] || nx_die 'invalid unit cgroup'
        nx_assert_plain_path "/sys/fs/cgroup$group"
        if [[ -d /sys/fs/cgroup$group ]]; then
            grep -Fxq 'populated 0' "/sys/fs/cgroup$group/cgroup.events" || nx_die 'a child process remains in a Nexora unit cgroup'
        fi
    fi
done

listing=''; names=''
cleanup() {
    local status=$? file
    trap - EXIT INT TERM
    for file in "$listing" "$names"; do
        if [[ -n $file && $file == /var/lib/nexora-restore/.archive-* && -f $file && ! -L $file ]]; then
            rm -- "$file" || status=1
        fi
    done
    [[ $status == 0 ]] || printf 'Nexora: rehearsal failed; any created target/database were preserved for inspection.\n' >&2
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
listing=$(mktemp /var/lib/nexora-restore/.archive-list.XXXXXXXX)
names=$(mktemp /var/lib/nexora-restore/.archive-names.XXXXXXXX)
# GNU tar escapes control bytes and quotes unusual names. Every escaped byte,
# link type, special inode and path outside these three roots is rejected.
tar --list --gzip --force-local --verbose --numeric-owner --full-time --quoting-style=escape \
    --file="$backup/payload.tar.gz" > "$listing" || nx_die 'unable to list payload archive'
have_storage=no; have_keys=no; have_config=no
while IFS= read -r line || [[ -n $line ]]; do
    [[ $line =~ ^([-d])[rwx-]{9}[[:space:]]+[0-9]+/[0-9]+[[:space:]]+[0-9]+[[:space:]]+[0-9]{4}-[0-9]{2}-[0-9]{2}[[:space:]]+[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]+)?[[:space:]]+(.+)$ ]] ||
        nx_die 'archive contains links, special files, unsafe modes or malformed names'
    kind=${BASH_REMATCH[1]}; member=${BASH_REMATCH[3]}
    if [[ $kind == d ]]; then
        [[ $member == */ ]] || nx_die 'archive directory lacks a canonical trailing slash'
        member=${member%/}
    else
        [[ $member != */ ]] || nx_die 'archive file name is malformed'
    fi
    [[ $member =~ ^(storage|keys|config)(/[A-Za-z0-9_.-]+)*$ && $member != *'/../'* && \
        $member != */.. && $member != *'/./'* && $member != */. ]] || nx_die 'archive path traversal or unexpected root'
    case $member in
        storage) [[ $kind == d ]] || nx_die 'storage root must be a directory'; have_storage=yes ;;
        keys) [[ $kind == d ]] || nx_die 'keys root must be a directory'; have_keys=yes ;;
        config) [[ $kind == d ]] || nx_die 'config root must be a directory'; have_config=yes ;;
    esac
    printf '%s\n' "$member" >> "$names"
done < "$listing"
[[ $have_storage == yes && $have_keys == yes && $have_config == yes ]] || nx_die 'archive lacks a required root'
sort --output="$names" -- "$names"
awk 'NR > 1 && $0 == previous { exit 1 } { previous=$0 }' "$names" || nx_die 'duplicate archive members are forbidden'

pg() {
    runuser -u postgres -- env -i PATH=/usr/bin:/bin LC_ALL=C PGPASSFILE=/dev/null PGSERVICEFILE=/dev/null \
        "$@" --host=/run/postgresql --port=5432 --username=postgres --no-password
}
query_admin() {
    pg psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --dbname=postgres --command "$1"
}
[[ $(query_admin "SELECT current_setting('server_version_num')::integer / 10000;") == 18 ]] || nx_die 'PostgreSQL server 18 is required'
[[ $(query_admin "SELECT count(*) FROM pg_database WHERE datname='$database';") == 0 ]] || nx_die 'target database already exists; the script never drops or cleans a database'
[[ $(query_admin "SELECT EXISTS (SELECT 1 FROM pg_roles r WHERE r.rolname='nexora_restore' AND NOT r.rolsuper AND NOT r.rolcanlogin AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication AND NOT r.rolbypassrls AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid));") == t ]] ||
    nx_die 'pre-create the dedicated unprivileged NOLOGIN nexora_restore owner without role memberships'

mkdir --mode=0700 -- "$target_root"
mkdir --mode=0700 -- "$target_root/metadata"
: > "$target_root/metadata/restore.log"
cp -- "$backup/manifest.txt" "$backup/release-id.txt" "$backup/release.json" "$backup/release-SHA256SUMS" \
    "$backup/migrations.txt" "$backup/database-settings.txt" "$backup/SHA256SUMS" "$target_root/metadata/"
tar --extract --gzip --force-local --file="$backup/payload.tar.gz" --directory="$target_root" \
    --no-same-owner --no-same-permissions --keep-old-files --no-overwrite-dir 2>> "$target_root/metadata/restore.log" ||
    nx_die 'archive extraction failed; inspect the protected restore.log'
nx_assert_tree_plain "$target_root"
find "$target_root" -type d -exec chmod 0700 -- {} +
find "$target_root" -type f -exec chmod 0600 -- {} +
pg createdb --template=template0 --encoding=UTF8 --owner=nexora_restore "$database" \
    2>> "$target_root/metadata/restore.log" || nx_die 'database creation failed; no existing database was modified'
pg psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname=postgres \
    --command "REVOKE ALL ON DATABASE \"$database\" FROM PUBLIC;" \
    >/dev/null 2>> "$target_root/metadata/restore.log" || nx_die 'unable to restrict rehearsal database access'
pg psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --dbname="$database" \
    --command 'SELECT pg_encoding_to_char(encoding) || chr(9) || datlocprovider || chr(9) || datcollate || chr(9) || datctype FROM pg_database WHERE datname=current_database();' \
    > "$target_root/metadata/restored-database-settings.txt" 2>> "$target_root/metadata/restore.log"
cmp -s -- "$backup/database-settings.txt" "$target_root/metadata/restored-database-settings.txt" ||
    nx_die 'isolated cluster locale/provider/encoding differs; target was left empty, use a matching cluster and a new id'
# Root opens the protected dump for stdin. SET ROLE confines dump SQL to the
# dedicated database owner; no production ownership/ACL/create/clean is replayed.
pg pg_restore --dbname="$database" --role=nexora_restore --no-owner --no-acl --single-transaction --exit-on-error \
    < "$backup/database.dump" >/dev/null 2>> "$target_root/metadata/restore.log" ||
    nx_die 'database restoration failed; inspect the protected restore.log'
pg psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --dbname="$database" \
    --command 'SELECT "MigrationId" || chr(9) || "ProductVersion" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";' \
    > "$target_root/metadata/restored-migrations.txt" 2>> "$target_root/metadata/restore.log"
cmp -s -- "$backup/migrations.txt" "$target_root/metadata/restored-migrations.txt" || nx_die 'restored EF migration history differs'
# Restored counters may overlap cursors already saved by clients. Rotate each
# owner's epoch before any client connects, forcing a fresh library snapshot.
# Older releases without the sync tables remain valid rehearsal inputs.
pg psql --no-psqlrc --set=ON_ERROR_STOP=1 --dbname="$database" \
    --command 'SET ROLE nexora_restore; DO $$ BEGIN IF to_regclass('\''public."AssetSyncStates"'\'') IS NOT NULL THEN UPDATE public."AssetSyncStates" SET "Epoch" = gen_random_uuid(); END IF; END $$;' \
    >/dev/null 2>> "$target_root/metadata/restore.log" || nx_die 'unable to rotate restored synchronization epochs'
printf 'Isolated data restoration completed: %s\nDatabase: %s\n' "$target_root" "$database"
printf '%s\n' 'Archived configuration is inert and root-only. Prepare a fresh isolated runtime configuration before manual validation.'
