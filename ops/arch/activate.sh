#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
export PATH=/usr/bin:/bin LC_ALL=C
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$script_dir/common.sh"

usage() {
    printf '%s\n' 'Usage: activate.sh --release-id ID' \
        'Stops both services, atomically switches current, and restarts only services previously active.' \
        'Never applies migrations. On failure, leaves services stopped for operator review.'
}
release_id=''
while (($#)); do
    case $1 in
        --release-id) (($# >= 2)) || nx_die 'missing release-id'; release_id=$2; shift 2;;
        --help|-h) usage; exit 0;;
        *) usage >&2; nx_die 'unknown argument';;
    esac
done
[[ -n $release_id ]] || { usage >&2; nx_die 'release-id is required'; }
nx_require_root
for cmd in systemctl stat readlink ln mv rm flock grep; do nx_require_command "$cmd"; done
nx_assert_release_id "$release_id"
nx_assert_owned_dir /opt/nexora root root 755
nx_assert_owned_dir /opt/nexora/releases root root 755
nx_assert_plain_path /opt/nexora/.deploy.lock
if [[ -e /opt/nexora/.deploy.lock ]]; then
    [[ -f /opt/nexora/.deploy.lock && $(stat -c '%U:%G:%a:%h' -- /opt/nexora/.deploy.lock) == root:root:600:1 ]] || nx_die 'unsafe deployment lock'
fi
exec 9>/opt/nexora/.deploy.lock
flock -n 9 || nx_die 'another installation or activation is running'
old_target=''
if [[ -L /opt/nexora/current ]]; then
    [[ $(stat -c '%U:%G' -- /opt/nexora/current) == root:root ]] || nx_die 'current link must be owned by root'
    old_target=$(readlink -- /opt/nexora/current)
    [[ $old_target == /opt/nexora/releases/* ]] || nx_die 'current points outside the release layout'
    nx_assert_release_id "${old_target#/opt/nexora/releases/}"
    nx_assert_owned_dir "$old_target" root root 755
elif [[ -e /opt/nexora/current ]]; then
    nx_die 'current exists and is not a root-managed symlink'
fi
"$script_dir/verify-host.sh" --release-id "$release_id"
declare -A was_active=() old_cgroup=()
for unit in nexora-api.service nexora-worker.service; do
    state=$(systemctl show --value --property=ActiveState "$unit")
    case $state in active) was_active["$unit"]=1;; inactive|failed) was_active["$unit"]=0;; *) nx_die 'service state is transitional or unknown; retry after operator review';; esac
    old_cgroup["$unit"]=$(systemctl show --value --property=ControlGroup "$unit")
    [[ -z ${old_cgroup["$unit"]} || ${old_cgroup["$unit"]} == "/system.slice/$unit" ]] || nx_die 'unexpected service control group'
done

stopped=0 switched=0 completed=0
temporary_link="/opt/nexora/.current-$release_id-$$"
[[ ! -e $temporary_link && ! -L $temporary_link ]] || nx_die 'temporary activation link already exists'
cleanup() {
    local result=$?
    if ((completed == 0 && stopped == 1)); then
        systemctl stop nexora-api.service nexora-worker.service >/dev/null 2>&1 || true
        # Restore only the pointer on a failed activation. Starting old code
        # after a migration requires an explicit compatibility decision.
        if ((switched == 1)); then
            if [[ -n $old_target ]]; then
                rm -f -- "$temporary_link"
                ln -s -- "$old_target" "$temporary_link" && mv -Tf -- "$temporary_link" /opt/nexora/current || true
            elif [[ -L /opt/nexora/current ]]; then
                rm -f -- /opt/nexora/current
            fi
        fi
        printf '%s\n' 'Nexora: activation failed; services remain stopped. Review the current link, migration compatibility and journal.' >&2
    fi
    [[ ! -L $temporary_link ]] || rm -f -- "$temporary_link"
    return "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
stopped=1
systemctl stop nexora-api.service nexora-worker.service
for unit in nexora-api.service nexora-worker.service; do
    state=$(systemctl show --value --property=ActiveState "$unit")
    [[ $state == inactive || $state == failed ]] || nx_die 'service did not stop'
    [[ $(systemctl show --value --property=MainPID "$unit") == 0 && $(systemctl show --value --property=ControlPID "$unit") == 0 ]] || nx_die 'service still has a main/control process'
    cgroup=${old_cgroup["$unit"]}
    if [[ -n $cgroup ]]; then
        [[ $cgroup == /system.slice/nexora-* && $cgroup != *..* && $cgroup != *$'\n'* ]] || nx_die 'unexpected service control group'
        if [[ -e /sys/fs/cgroup$cgroup/cgroup.events ]]; then
            grep -qx 'populated 0' "/sys/fs/cgroup$cgroup/cgroup.events" || nx_die 'service cgroup still contains processes'
        fi
    fi
done
ln -s -- "/opt/nexora/releases/$release_id" "$temporary_link"
mv -Tf -- "$temporary_link" /opt/nexora/current
switched=1
"$script_dir/verify-host.sh" --release-id "$release_id"
for unit in nexora-api.service nexora-worker.service; do
    if [[ ${was_active["$unit"]} == 1 ]]; then
        systemctl start "$unit"
        systemctl is-active --quiet "$unit" || nx_die 'a previously active service failed to start'
    fi
done
completed=1
printf 'Activated release %s. Previously inactive services remain inactive; migrations were not run.\n' "$release_id"
printf '%s\n' 'Check /health/ready and authenticated upload/image flows before accepting the deployment.'
