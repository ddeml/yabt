#!/usr/bin/env bash
set -euo pipefail
umask 077

fail() {

    printf 'Smoke check failed: %s\n' "$*" >&2
    exit 1
}

if (( $# < 2 || $# > 3 )); then
    printf 'Usage: bash run-smoke.sh <published-Yabt.Cli.dll> <new-directory> [--allow-ext4]\n' >&2
    exit 64
fi
[[ "${3:-}" == '' || "${3:-}" == --allow-ext4 ]] || exit 64
[[ "$(uname -s)" == Linux ]] || fail 'A Linux host kernel is required.'
(( EUID != 0 )) || fail 'Run the checks as an ordinary user, not root.'
for tool in dotnet realpath stat findmnt timeout tee cmp find sort touch dd sha256sum grep; do
    command -v "$tool" >/dev/null || fail "Required command is unavailable: $tool"
done

cli_path="$(realpath -e -- "$1")"
[[ -f "$cli_path" && "$cli_path" == *.dll ]] || fail 'Supply the published CLI DLL.'
[[ "$2" == /* ]] || fail 'The new fixture directory must use an absolute path.'
[[ ! "$2" =~ [[:cntrl:]] ]] || fail 'Control characters are not supported in fixture paths.'
fixture_parent="$(realpath -e -- "$(dirname -- "$2")")"
fixture_name="$(basename -- "$2")"
[[ "$fixture_name" != . && "$fixture_name" != .. && "$fixture_name" != / ]] || exit 64
fixture_root="$fixture_parent/$fixture_name"
[[ ! "$fixture_root" =~ [[:cntrl:]] ]] || fail 'Resolved fixture paths must not contain control characters.'
[[ ! -e "$fixture_root" && ! -L "$fixture_root" ]] || fail 'The fixture directory already exists.'
[[ -d "$fixture_parent" && -w "$fixture_parent" && -x "$fixture_parent" ]] ||
    fail 'The fixture parent must already exist and be writable.'
filesystem="$(findmnt -n -T "$fixture_parent" -o FSTYPE)"
scope=btrfs-smoke
case "$filesystem" in
    btrfs) ;;
    ext4)
        [[ "${3:-}" == --allow-ext4 ]] || fail 'ext4 requires explicit --allow-ext4 developer validation.'
        scope=ext4-developer-validation
        ;;
    *) fail "Expected actual Btrfs storage; found '$filesystem'." ;;
esac
command_timeout="${YABT_SMOKE_COMMAND_TIMEOUT_SECONDS:-120}"
[[ "$command_timeout" =~ ^[1-9][0-9]*$ ]] || fail 'The command timeout must be a positive integer.'
revision="${YABT_SOURCE_REVISION:-not-supplied}"
[[ "$revision" == not-supplied || "$revision" =~ ^[0-9a-fA-F]{40}$ ]] ||
    fail 'YABT_SOURCE_REVISION must be a complete Git commit id when supplied.'

mkdir -- "$fixture_root"
[[ "$(stat -c %u -- "$fixture_root")" == "$EUID" ]] || fail 'The new fixture is not owned by this user.'
source_root="$fixture_root/source"
archive_root="$fixture_root/archive"
results="$fixture_root/results"
mkdir -- "$source_root" "$results" "$fixture_root/scratch" "$fixture_root/progress" "$fixture_root/state"
mkdir -- "$fixture_root/scratch/runtime" "$fixture_root/scratch/yabt"
printf 'YABT Synology CLI smoke fixture\n' > "$fixture_root/.yabt-smoke-owner"
started_utc="$(date -u +%FT%TZ)"
phase=environment
finish() {

    local exit_code=$?
    trap - EXIT
    {
        printf 'scope=%s\nfilesystem=%s\nsource_revision=%s\n' "$scope" "$filesystem" "$revision"
        printf 'started_utc=%s\nfinished_utc=%s\n' "$started_utc" "$(date -u +%FT%TZ)"
        printf 'phase=%s\nexit_code=%s\n' "$phase" "$exit_code"
    } > "$results/run-status.txt"
    printf '\nEvidence and fixtures retained at %s\n' "$fixture_root"
    exit "$exit_code"
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

export DOTNET_ENVIRONMENT=Production DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR="$fixture_root/scratch/runtime" XDG_STATE_HOME="$fixture_root/state"
export TemporaryContent__TemporaryDirectory="$fixture_root/scratch/yabt"
export TemporaryContent__MemoryThresholdBytes=0 TemporaryContent__TotalMemoryBudgetBytes=0
export Sync__ProgressDirectory="$fixture_root/progress" Sync__ProgressEnabled=true
export Sync__ProgressFlushUnitCount=1 Sync__ProgressFlushIntervalSeconds=1
export Sync__RestoreMaximumConcurrency=1 Logging__LogLevel__Default=Debug
cd -- "$fixture_root"
{
    printf 'Scope: %s\nFixture: %s\nSource revision: %s\n' "$scope" "$fixture_root" "$revision"
    printf 'CLI SHA-256: '
    sha256sum -- "$cli_path"
    uname -a
    id
    [[ ! -r /etc/os-release ]] || cat /etc/os-release
    findmnt -T "$fixture_root" -o TARGET,SOURCE,FSTYPE,OPTIONS
    stat -f -c 'Filesystem magic: %t; statfs type: %T' -- "$fixture_root"
    printf 'Payload threshold/budget: 0 bytes; restore concurrency: 1\n'
    printf 'Per-command timeout: %s seconds\n' "$command_timeout"
    dotnet --info
} > "$results/environment.txt" 2>&1
printf 'case\toutcome\n' > "$results/cases.tsv"

passed() {

    printf '%s\tpassed\n' "$1" >> "$results/cases.tsv"
}

run_cli() {

    phase="$1"
    shift
    printf '\nRunning %s\n' "$phase"
    set +e
    timeout --signal=TERM --kill-after=10s "${command_timeout}s" \
        dotnet "$cli_path" "$@" 2>&1 | tee "$results/$phase.log" >/dev/null
    local statuses=("${PIPESTATUS[@]}")
    set -e
    if (( statuses[0] != 0 || statuses[1] != 0 )); then
        printf '%s\tfailed\n' "$phase" >> "$results/cases.tsv"
        fail "$phase failed; see its retained log."
    fi
    if grep -F -e 'Local operation progress is unavailable' \
        -e 'Could not finish releasing the disposable local progress journal' \
        "$results/$phase.log" >/dev/null; then
        fail "$phase completed without healthy local progress support."
    fi
    passed "$phase"
    printf 'Passed %s\n' "$phase"
}

json_string() {

    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    printf '"%s"' "$value"
}

assert_clean_operation() {

    local root="$1"
    [[ ! -e "$root/.yabt-change-manifest.invalid" &&
       ! -e "$root/.yabt-logical-state-manifest.invalid" &&
       ! -e "$root/.yabt-history-manifest.invalid" &&
       ! -e "$root/.yabt-hist/.yabt-history-manifest.invalid" &&
       ! -e "$root/.yabt-tmp/archive-mutation-lock.json" ]] ||
        fail "Successful operation left recovery or lock evidence in $root."
    [[ -z "$(find "$fixture_root/scratch/yabt" -mindepth 1 -print -quit)" ]] ||
        fail 'Successful operation left temporary content or indexes.'
    [[ -z "$(find "$fixture_root/progress" -type f ! -name '*.lock' -print -quit)" ]] ||
        fail 'Successful operation left an unfinished progress journal.'
}

assert_history() {

    local relative_path="$1"
    local expected="$2"
    local candidate
    while IFS= read -r -d '' candidate; do
        if cmp -s -- "$candidate" "$expected"; then return; fi
    done < <(find "$archive_root/.yabt-hist" -type f -path "*/$relative_path" -print0)
    fail "History does not preserve the original $relative_path."
}

assert_restored_timestamps() {

    local restored="$1"
    local original
    while IFS= read -r -d '' original; do
        [[ "$(stat -c %y -- "$original")" == \
           "$(stat -c %y -- "$restored/${original#"$source_root/"}")" ]] ||
            fail "Restored timestamp differs for ${original#"$source_root/"}."
    done < <(find "$source_root" -type f ! -path "$source_root/.yabt-root.json" -print0)
}

live_inventory() {

    find "$archive_root" \( -path "$archive_root/.yabt-tmp" -o \
        -path "$archive_root/.yabt-hist" \) -prune -o \
        -type f ! -name '.yabt-change-manifest.*' -printf '%P\t%i\t%s\t%T@\n' |
        LC_ALL=C sort
}

phase=fixture
cat > "$source_root/.yabt-root.json" <<EOF
{
  "documentType": "yabt.backupRoot",
  "schemaVersion": 1,
  "archiveId": "synology-smoke-$started_utc",
  "rootRole": "source",
  "createdAtUtc": "2024-01-02T03:04:05Z",
  "defaultStoreId": "test-archive",
  "changeManifestCompression": "none",
  "layout": { "livePrefix": "", "histPrefix": ".yabt-hist" },
  "stores": [
    { "id": "test-archive", "kind": "fileSystem", "rootPath": $(json_string "$archive_root") }
  ]
}
EOF
printf '{"format":"mirror"}\n' > "$source_root/.yabt-policy.json"
mkdir -- "$source_root/empty" "$source_root/nested"
printf 'original payload\n' > "$source_root/changed.txt"
printf 'deleted payload\n' > "$source_root/deleted.txt"
printf 'Unicode and punctuation\n' > "$source_root/nested/résumé #100%.txt"
dd if=/dev/urandom of="$source_root/payload.bin" bs=1048576 count=16 status=none
find "$source_root" -type f -exec touch -d '2024-01-02 03:04:05.123456700 UTC' -- {} +
cp -- "$source_root/changed.txt" "$results/expected-changed-v1.txt"
cp -- "$source_root/deleted.txt" "$results/expected-deleted.txt"

run_cli initial-backup backup "$source_root"
assert_clean_operation "$archive_root"
[[ -n "$(find "$fixture_root/progress" -type f -name '*.sqlite.lock' -print -quit)" ]] ||
    fail 'No evidence that persistent progress was opened.'
passed native-controls-and-progress
live_inventory > "$results/before-unchanged.tsv"
run_cli unchanged-backup backup "$source_root"
live_inventory > "$results/after-unchanged.tsv"
cmp -- "$results/before-unchanged.tsv" "$results/after-unchanged.tsv"
[[ ! -e "$archive_root/.yabt-hist" ]] || fail 'Unchanged backup unexpectedly created history.'
passed unchanged-live-inventory
run_cli initial-verify verify "$source_root" --byte-for-byte
run_cli initial-restore restore "$source_root" --destination-root "$fixture_root/restored-initial"
run_cli initial-verify-restore verify-restore "$source_root" --destination-root "$fixture_root/restored-initial"
assert_restored_timestamps "$fixture_root/restored-initial"
assert_clean_operation "$fixture_root/restored-initial"
passed initial-timestamps-and-cleanup

phase=incremental-fixture
printf 'replacement payload with a different length\n' > "$source_root/changed.txt"
printf 'new payload\n' > "$source_root/added.txt"
touch -d '2024-01-03 03:04:05.123456700 UTC' -- "$source_root/changed.txt" "$source_root/added.txt"
rm -- "$source_root/deleted.txt"
run_cli incremental-backup backup "$source_root"
cmp -- "$source_root/changed.txt" "$archive_root/changed.txt"
[[ ! -e "$archive_root/deleted.txt" ]] || fail 'The deleted fixture remains live.'
assert_history changed.txt "$results/expected-changed-v1.txt"
assert_history deleted.txt "$results/expected-deleted.txt"
assert_clean_operation "$archive_root"
passed incremental-live-and-history
run_cli incremental-verify verify "$source_root" --byte-for-byte
run_cli incremental-restore restore "$source_root" --destination-root "$fixture_root/restored-incremental"
run_cli incremental-verify-restore verify-restore "$source_root" --destination-root "$fixture_root/restored-incremental"
assert_restored_timestamps "$fixture_root/restored-incremental"
assert_clean_operation "$fixture_root/restored-incremental"
passed incremental-timestamps-and-cleanup

phase=zip-fixture
mkdir -- "$source_root/packaged" "$source_root/packaged/empty"
printf '{"format":"zip"}\n' > "$source_root/packaged/.yabt-policy.json"
cp -- "$source_root/payload.bin" "$source_root/packaged/large payload.bin"
printf 'ZIP Unicode payload\n' > "$source_root/packaged/Grüße.txt"
find "$source_root/packaged" -type f -exec touch -d '2024-01-04 03:04:05.123456700 UTC' -- {} +
run_cli zip-backup backup "$source_root"
[[ ! -e "$archive_root/packaged" ]] || fail 'The ZIP folder was not projected beside its parent.'
[[ -n "$(find "$archive_root" -maxdepth 1 -type f -name 'packaged.*.zip' -print -quit)" ]] ||
    fail 'No ZIP artifact was created.'
assert_clean_operation "$archive_root"
passed zip-artifact-and-forced-disk-cleanup
run_cli zip-verify verify "$source_root" --byte-for-byte
run_cli zip-restore restore "$source_root" --destination-root "$fixture_root/restored-zip"
run_cli zip-verify-restore verify-restore "$source_root" --destination-root "$fixture_root/restored-zip"
assert_restored_timestamps "$fixture_root/restored-zip"
assert_clean_operation "$fixture_root/restored-zip"
passed zip-timestamps-and-cleanup
phase=complete
printf '\nCLI smoke checks passed (%s). This is not full deployment or crash-recovery acceptance.\n' "$scope"
