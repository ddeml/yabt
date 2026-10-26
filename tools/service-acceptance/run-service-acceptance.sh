#!/usr/bin/env bash
set -euo pipefail

(( $# == 2 )) || exit 64
source_root="$1"
results="$2"
[[ "$source_root" == /work/checkouts/* && -f "$source_root/yabt.sln" ]] || exit 64
[[ "$results" == /private-results ]] || exit 64
[[ -r /run/secrets/providers.json ]] || exit 64
mkdir -p -- "$results/tests" "$results/diagnostics"
bash "$source_root/tools/linux-acceptance/preflight.sh" "$source_root" "$results"
export YABT_SERVICE_ACCEPTANCE=1
export YABT_SERVICE_ACCEPTANCE_SETTINGS=/run/secrets/providers.json
export YABT_SERVICE_ACCEPTANCE_PRIVATE_RESULTS="$results/diagnostics"
cd -- "$source_root"
status=0
for project in Yabt.AzureBlob.Tests Yabt.WebDav.Tests Yabt.Sync.Tests; do
    mkdir -- "$results/tests/$project"
    set +e
    timeout --signal=TERM --kill-after=30s 1200s \
        dotnet test "tests/$project/$project.csproj" --configuration Release \
        --verbosity minimal -m:1 --disable-build-servers \
        --filter 'TestCategory=DeployedService' --logger trx \
        --results-directory "$results/tests/$project"
    project_status=$?
    set -e
    printf '%s\n' "$project_status" > "$results/$project.exit-code"
    (( project_status == 0 )) || status=1
done
printf 'complete\n' > "$results/run-status.txt"
exit "$status"
