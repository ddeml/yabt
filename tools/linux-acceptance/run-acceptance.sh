#!/usr/bin/env bash
set -euo pipefail

if (( $# != 1 )); then
    printf 'Usage: YABT_SOURCE_REVISION=<commit> bash run-acceptance.sh <new-result-directory>\n' >&2
    exit 64
fi
[[ "${YABT_SOURCE_REVISION:-}" =~ ^[0-9a-fA-F]{40}$ ]] || {
    printf 'YABT_SOURCE_REVISION must identify the committed source snapshot.\n' >&2
    exit 64
}
test_timeout="${YABT_ACCEPTANCE_TIMEOUT_SECONDS:-1800}"
[[ "$test_timeout" =~ ^[1-9][0-9]*$ ]] || {
    printf 'YABT_ACCEPTANCE_TIMEOUT_SECONDS must be a positive integer.\n' >&2
    exit 64
}

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source_root="$(cd -- "$script_directory/../.." && pwd -P)"
[[ "$1" == /* ]] || {
    printf 'The result directory must be an absolute Linux path.\n' >&2
    exit 64
}
result_parent="$(realpath -e -- "$(dirname -- "$1")")"
result_name="$(basename -- "$1")"
[[ "$result_name" != . && "$result_name" != .. ]] || exit 64
result_directory="$result_parent/$result_name"
[[ ! -e "$result_directory" && ! -L "$result_directory" ]] || {
    printf 'Use a new result directory; existing evidence is never replaced.\n' >&2
    exit 64
}
[[ "$result_directory/" != "$source_root/"* ]] || {
    printf 'Keep acceptance results outside the source checkout.\n' >&2
    exit 64
}
mkdir -- "$result_directory"
started_utc="$(date -u +%FT%TZ)"
phase=preflight
finish() {
    local exit_code=$?
    trap - EXIT
    {
        printf 'source_revision=%s\n' "$YABT_SOURCE_REVISION"
        printf 'source_directory=%s\n' "$source_root"
        printf 'started_utc=%s\n' "$started_utc"
        printf 'finished_utc=%s\n' "$(date -u +%FT%TZ)"
        printf 'phase=%s\n' "$phase"
        printf 'exit_code=%s\n' "$exit_code"
    } > "$result_directory/run-status.txt"
    exit "$exit_code"
}
trap finish EXIT

set +e
bash "$script_directory/preflight.sh" "$source_root" "$result_directory" 2>&1 |
    tee "$result_directory/environment.txt"
preflight_status=("${PIPESTATUS[@]}")
set -e
(( preflight_status[0] == 0 )) || exit "${preflight_status[0]}"
(( preflight_status[1] == 0 )) || exit "${preflight_status[1]}"

printf '%s\n' "$test_timeout" > "$result_directory/test-timeout-seconds.txt"
cat > "$result_directory/required-test-cases.tsv" <<'CASES'
className	methodName	passedRows
Yabt.Sync.Tests.PathIdentityTests	BackupRejectsSelectedUnixDirectoryWithLiteralBackslashBeforeSelectingAnotherBranch	1
Yabt.Sync.Tests.PathIdentityTests	RestoreRejectsExistingUnixLiteralBackslashEntryBeforeMutation	2
Yabt.Sync.Tests.ProcessCrashRecoveryTests	FreshProcessRecoversAfterForcedTermination	7
Yabt.FileSystem.Tests.LinuxFileSystemPermissionTests	EnsureReadyAsyncRejectsUnreadableRootWithoutCreatingPlumbing	1
Yabt.FileSystem.Tests.LinuxFileSystemPermissionTests	GetFolderItemsAsyncRejectsPermissionLostAfterReadiness	1
Yabt.FileSystem.Tests.LinuxFileSystemPermissionTests	OpenReadAsyncRejectsUnreadableFileWithoutChangingIt	1
CASES

phase=tests
cd -- "$source_root"
set +e
timeout --signal=TERM --kill-after=30s "${test_timeout}s" \
    dotnet test yabt.sln --configuration Release --verbosity minimal \
    -m:1 --disable-build-servers --logger trx \
    --results-directory "$result_directory/tests" \
    "-p:SourceRevisionId=$YABT_SOURCE_REVISION" 2>&1 |
    tee "$result_directory/test-output.log"
test_status=("${PIPESTATUS[@]}")
set -e
printf '%s\n' "${test_status[0]}" > "$result_directory/test-exit-code.txt"
(( test_status[0] == 0 )) || exit "${test_status[0]}"
(( test_status[1] == 0 )) || exit "${test_status[1]}"
phase=complete
printf '\nTest execution completed. Inspect TRX outcomes and required cases before declaring acceptance.\n'
