#!/usr/bin/env bash
# Manual, quiesced snapshot. This script never reads environment files as code.
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin LC_ALL=C
unset TAR_OPTIONS GZIP
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

usage() {
    printf '%s\n' 'Usage: sudo bash backup.sh --backup-root /mnt/nexora-backups' \
        'The existing backup root must be root:root 0700, outside Nexora and Nextcloud.'
}
backup_root=''
while (($#)); do
    case $1 in
        --backup-root) (($# >= 2)) || nx_die 'missing --backup-root value'; backup_root=$2; shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) usage >&2; nx_die 'unknown argument' ;;
    esac
done
[[ -n $backup_root ]] || { usage >&2; nx_die '--backup-root is required'; }
nx_require_root
for command in systemctl runuser env psql pg_dump tar gzip jq find stat sha256sum readlink date cp mv grep tail cut cat mkdir sleep flock; do
    nx_require_command "$command"
done

# No writable ancestors: root is trusted; a mount must already be provisioned.
assert_backup_root() {
    local path=$1 parent=$1 permissions
    nx_assert_plain_path "$path"
    [[ $path =~ ^/[A-Za-z0-9_./-]+$ ]] || nx_die 'backup root must use portable ASCII path components'
    [[ ${path,,} != *'/nextcloud/'* && ${path,,} != */nextcloud ]] || nx_die 'Nextcloud is not a backup destination'
    case $path in
        /opt/nexora|/opt/nexora/*|/etc/nexora|/etc/nexora/*|/var/lib/nexora|/var/lib/nexora/*|/srv/nexora|/srv/nexora/*|/var/lib/nexora-restore|/var/lib/nexora-restore/*)
            nx_die 'backup destination overlaps a managed Nexora tree' ;;
    esac
    nx_assert_owned_dir "$path" root root 700
    while [[ $parent != / ]]; do
        [[ $(stat -c '%u' -- "$parent") == 0 ]] || nx_die 'backup ancestors must be owned by root'
        permissions=$(stat -c '%a' -- "$parent")
        (( (8#$permissions & 0022) == 0 )) || nx_die 'backup ancestors must not be writable by group or others'
        parent=${parent%/*}; [[ -n $parent ]] || parent=/
    done
}
assert_backup_root "$backup_root"
nx_assert_owned_dir /opt/nexora root root 755
nx_assert_owned_dir /opt/nexora/releases root root 755
nx_assert_plain_path /opt/nexora/.deploy.lock
if [[ -e /opt/nexora/.deploy.lock ]]; then
    [[ -f /opt/nexora/.deploy.lock && $(stat -c '%U:%G:%a:%h' -- /opt/nexora/.deploy.lock) == root:root:600:1 ]] || nx_die 'unsafe deployment lock'
fi
exec 9>/opt/nexora/.deploy.lock
flock -n 9 || nx_die 'another installation, activation, backup or restoration is running'
[[ -L /opt/nexora/current ]] || nx_die 'current must be the managed release symlink'
[[ $(stat -c '%U:%G' -- /opt/nexora/current) == root:root ]] || nx_die 'current link must be owned by root'
release_dir=$(readlink -f -- /opt/nexora/current) || nx_die 'unable to resolve current release'
[[ $release_dir =~ ^/opt/nexora/releases/([A-Za-z0-9][A-Za-z0-9._-]{0,63})$ ]] || nx_die 'current points outside the release tree'
release_id=${BASH_REMATCH[1]}
nx_assert_release_id "$release_id"
nx_assert_owned_dir "$release_dir" root root 755
nx_verify_sha256_manifest "$release_dir"
[[ $(cat -- "$release_dir/release-id.txt") == "$release_id" ]] || nx_die 'release-id.txt does not match current'
jq -e --arg id "$release_id" '.schemaVersion == 1 and .releaseId == $id and .runtimeIdentifier == "linux-x64"' \
    "$release_dir/release.json" >/dev/null || nx_die 'unsupported release manifest'
last_migration=$(jq -er '.lastMigration' "$release_dir/release.json") || nx_die 'release migration is missing'
[[ $last_migration =~ ^[0-9]{14}_[A-Za-z0-9_]+$ ]] || nx_die 'invalid release migration'
[[ $(pg_dump --version) =~ \(PostgreSQL\)\ 18\. ]] || nx_die 'pg_dump 18 is required'

nx_assert_owned_dir /srv/nexora nexora nexora 700
nx_assert_owned_dir /var/lib/nexora/keys nexora nexora 700
nx_assert_owned_dir /etc/nexora root nexora 750
for file in common.env api.env worker.env; do
    nx_assert_plain_path "/etc/nexora/$file"
    [[ -f /etc/nexora/$file && $(stat -c '%U:%G:%a' -- "/etc/nexora/$file") == root:root:600 ]] ||
        nx_die 'service environment files must be regular root:root 0600 files'
done
[[ -f /etc/nexora/data-protection.pfx && $(stat -c '%U:%G:%a' -- /etc/nexora/data-protection.pfx) == root:nexora:640 ]] ||
    nx_die 'Data Protection PFX must be a regular root:nexora 0640 file'
for tree in /srv/nexora /var/lib/nexora/keys /etc/nexora; do
    nx_assert_tree_plain "$tree"
    while IFS= read -r -d '' entry; do
        relative=${entry#"$tree"}
        [[ $relative =~ ^(/[A-Za-z0-9_.-]+)*$ && $relative != *'/../'* && $relative != *'/./'* ]] ||
            nx_die 'snapshot trees contain a filename outside the portable archive contract'
    done < <(find "$tree" -print0)
done

declare -A initially_active=() old_cgroup=()
for unit in nexora-api.service nexora-worker.service; do
    [[ $(systemctl show --property=LoadState --value "$unit") == loaded ]] || nx_die 'both Nexora units must be installed'
    state=$(systemctl show --property=ActiveState --value "$unit")
    case $state in
        active) initially_active[$unit]=yes ;;
        inactive|failed) initially_active[$unit]=no ;;
        *) nx_die 'a Nexora unit is transitioning; wait before taking a snapshot' ;;
    esac
    old_cgroup[$unit]=$(systemctl show --property=ControlGroup --value "$unit")
    [[ -z ${old_cgroup[$unit]} || ${old_cgroup[$unit]} =~ ^/[A-Za-z0-9_.@/-]+$ ]] || nx_die 'unexpected unit cgroup path'
    [[ ${old_cgroup[$unit]} != *'/../'* && ${old_cgroup[$unit]} != *'/./'* ]] || nx_die 'invalid unit cgroup path'
done

created_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
snapshot_id="$(date -u +%Y%m%dT%H%M%SZ)_$release_id"
snapshot="$backup_root/$snapshot_id.incomplete"
completed="$backup_root/$snapshot_id"
[[ ! -e $snapshot && ! -L $snapshot && ! -e $completed && ! -L $completed ]] || nx_die 'snapshot destination already exists'
mkdir --mode=0700 -- "$snapshot"
: > "$snapshot/backup.log"
stop_attempted=no
snapshot_complete=no
finish() {
    local status=$? unit restart_failed=no
    trap - EXIT INT TERM
    if [[ $stop_attempted == yes ]]; then
        for unit in nexora-worker.service nexora-api.service; do
            if [[ ${initially_active[$unit]} == yes ]]; then
                if ! systemctl start "$unit"; then
                    printf 'Nexora: failed to restart %s; inspect systemctl status.\n' "$unit" >&2
                    restart_failed=yes
                fi
            fi
        done
    fi
    if [[ $restart_failed == yes ]]; then status=1; fi
    if [[ $status == 0 && $snapshot_complete == yes ]]; then
        printf 'Snapshot completed: %s\n' "$completed"
    else
        printf 'Nexora: backup failed or service restart failed; protected artifacts were preserved.\n' >&2
    fi
    exit "$status"
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

pg() {
    # Explicit Unix socket + peer; inherited credentials/services are discarded.
    runuser -u postgres -- env -i PATH=/usr/bin:/bin LC_ALL=C PGPASSFILE=/dev/null PGSERVICEFILE=/dev/null \
        "$@" --host=/run/postgresql --port=5432 --username=postgres --no-password
}
query() {
    pg psql --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --dbname=nexora \
        --command "$1" 2>> "$snapshot/backup.log"
}
[[ $(query "SELECT current_setting('server_version_num')::integer / 10000;") == 18 ]] || nx_die 'PostgreSQL server 18 is required'
[[ $(query "SELECT EXISTS (SELECT 1 FROM pg_database d JOIN pg_roles r ON r.oid=d.datdba WHERE d.datname='nexora' AND r.rolname='nexora_migrator' AND NOT r.rolsuper AND NOT r.rolcreaterole AND NOT r.rolcreatedb AND NOT r.rolreplication AND NOT r.rolbypassrls AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid)) AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner WHERE n.nspname='public' AND c.relkind IN ('r','p','S','v','m','f') AND r.rolname <> 'nexora_migrator');") == t ]] ||
    nx_die 'production database and application relations must be owned by the unprivileged nexora_migrator role'

stop_attempted=yes
systemctl stop nexora-api.service nexora-worker.service || nx_die 'service stop failed'
deadline=$((SECONDS + 90))
while :; do
    quiescent=yes
    for unit in nexora-api.service nexora-worker.service; do
        state=$(systemctl show --property=ActiveState --value "$unit")
        [[ $state == inactive || $state == failed ]] || quiescent=no
        [[ $(systemctl show --property=MainPID --value "$unit") == 0 && $(systemctl show --property=ControlPID --value "$unit") == 0 ]] || quiescent=no
        group=${old_cgroup[$unit]}
        if [[ -n $group && -d /sys/fs/cgroup$group ]]; then
            nx_assert_plain_path "/sys/fs/cgroup$group"
            [[ -f /sys/fs/cgroup$group/cgroup.events ]] || nx_die 'unified cgroup v2 is required'
            grep -Fxq 'populated 0' "/sys/fs/cgroup$group/cgroup.events" || quiescent=no
        fi
    done
    [[ $(query "SELECT count(*) FROM pg_stat_activity WHERE datname='nexora' AND pid <> pg_backend_pid();") == 0 ]] || quiescent=no
    [[ $quiescent == yes ]] && break
    ((SECONDS < deadline)) || nx_die 'API/Worker streams, child renderer or database connections did not quiesce within 90 seconds'
    sleep 1
done

query 'SELECT "MigrationId" || chr(9) || "ProductVersion" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";' > "$snapshot/migrations.txt"
[[ $(tail -n 1 "$snapshot/migrations.txt" | cut -f 1) == "$last_migration" ]] || nx_die 'database history does not match the release; apply migrations explicitly first'
query 'SELECT pg_encoding_to_char(encoding) || chr(9) || datlocprovider || chr(9) || datcollate || chr(9) || datctype FROM pg_database WHERE datname=current_database();' > "$snapshot/database-settings.txt"
# Root opens the protected output before runuser; dump bytes never reach a terminal.
pg pg_dump --format=custom --dbname=nexora > "$snapshot/database.dump" 2>> "$snapshot/backup.log" || nx_die 'pg_dump failed; inspect the protected backup.log'
tar --create --gzip --force-local --format=posix --numeric-owner --file="$snapshot/payload.tar.gz" --directory=/ \
    --transform='s,^srv/nexora,storage,' --transform='s,^var/lib/nexora/keys,keys,' --transform='s,^etc/nexora,config,' \
    srv/nexora var/lib/nexora/keys etc/nexora 2>> "$snapshot/backup.log" || nx_die 'payload archive failed; inspect the protected backup.log'
cp -- "$release_dir/release-id.txt" "$release_dir/release.json" "$snapshot/"
cp -- "$release_dir/SHA256SUMS" "$snapshot/release-SHA256SUMS"
cat > "$snapshot/manifest.txt" <<EOF
format=nexora-backup-v1
created_utc=$created_utc
release_id=$release_id
database=nexora
database_owner=nexora_migrator
postgres_major=18
storage_root=/srv/nexora
keys_root=/var/lib/nexora/keys
config_root=/etc/nexora
last_migration=$last_migration
api_was_active=${initially_active[nexora-api.service]}
worker_was_active=${initially_active[nexora-worker.service]}
EOF
(cd -- "$snapshot" && sha256sum -- backup.log database-settings.txt database.dump manifest.txt migrations.txt payload.tar.gz release-id.txt release.json release-SHA256SUMS > SHA256SUMS)
nx_verify_sha256_manifest "$snapshot"
mv --no-clobber --no-target-directory -- "$snapshot" "$completed"
[[ ! -e $snapshot && -d $completed ]] || nx_die 'unable to finalize the snapshot without overwriting'
snapshot_complete=yes
