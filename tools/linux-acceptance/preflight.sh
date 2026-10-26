#!/usr/bin/env bash
set -euo pipefail

fail() {
    printf 'Preflight failed: %s\n' "$*" >&2
    exit 1
}

if (( $# > 2 )); then
    printf 'Usage: bash preflight.sh [source-root] [result-directory]\n' >&2
    exit 64
fi

printf 'Checked UTC: %s\n' "$(date -u +%FT%TZ)"
uname -a
id
[[ "$(uname -s)" == Linux ]] || fail 'A Linux kernel is required.'
(( EUID != 0 )) || fail 'Run acceptance as an ordinary user, not root.'
[[ "$(getconf LONG_BIT)" == 64 ]] || fail 'The Linux provider requires 64-bit native APIs.'

kernel_release="$(uname -r)"
if [[ "$kernel_release" =~ ^([0-9]+)\.([0-9]+) ]]; then
    kernel_major="${BASH_REMATCH[1]}"
    kernel_minor="${BASH_REMATCH[2]}"
    (( kernel_major > 5 || (kernel_major == 5 && kernel_minor >= 8) )) ||
        fail 'Directory-link acceptance requires Linux 5.8 or newer statx mount identities.'
else
    fail 'The Linux kernel version could not be identified.'
fi

[[ -r /etc/os-release ]] || fail 'Distribution metadata is unavailable.'
cat /etc/os-release
for tool in dotnet findmnt realpath stat timeout tee; do
    command -v "$tool" >/dev/null || fail "Required command is unavailable: $tool"
done

source_root="$(realpath -e -- "${1:-.}")"
[[ -f "$source_root/yabt.sln" ]] || fail 'The source root does not contain yabt.sln.'
printf '\nSource root: %s\n' "$source_root"
printf 'Requested source revision: %s\n' "${YABT_SOURCE_REVISION:-not supplied}"
printf 'dotnet executable: %s\n' "$(command -v dotnet)"
sdk_info="$(DOTNET_CLI_UI_LANGUAGE=en dotnet --info)"
printf '%s\n' "$sdk_info"
[[ "$sdk_info" =~ OS\ Platform:[[:space:]]*Linux ]] ||
    fail 'The dotnet executable must report Linux as its OS platform.'
sdk_version="$(dotnet --version)"
[[ "$sdk_version" == 10.* ]] || fail 'Use a .NET 10 SDK for this acceptance environment.'

check_directory() {
    local label="$1"
    local requested="$2"
    [[ "$requested" == /* ]] || fail "$label must be an absolute Linux path."
    local resolved
    resolved="$(realpath -e -- "$requested")"
    [[ -d "$resolved" && -w "$resolved" && -x "$resolved" ]] ||
        fail "$label is not an accessible writable directory: $resolved"
    local filesystem
    filesystem="$(findmnt -n -T "$resolved" -o FSTYPE)"
    case "$filesystem" in
        ext2|ext3|ext4|tmpfs) ;;
        *) fail "$label uses unsupported acceptance storage '$filesystem': $resolved" ;;
    esac
    printf '\n%s: %s\n' "$label" "$resolved"
    findmnt -T "$resolved" -o TARGET,SOURCE,FSTYPE,OPTIONS
    stat -f -c 'Filesystem magic: %t; statfs type: %T' -- "$resolved"
    if [[ "$label" != Source && "$resolved/" == "$source_root/"* ]]; then
        fail "$label must be outside the source checkout."
    fi
}

check_directory Source "$source_root"
check_directory TMPDIR "${TMPDIR:?Set TMPDIR to Linux fixture storage.}"
check_directory XDG_STATE_HOME "${XDG_STATE_HOME:?Set XDG_STATE_HOME to Linux state storage.}"
check_directory NUGET_PACKAGES "${NUGET_PACKAGES:?Set NUGET_PACKAGES to Linux package storage.}"
check_directory DOTNET_CLI_HOME "${DOTNET_CLI_HOME:?Set DOTNET_CLI_HOME to Linux CLI storage.}"
check_directory UserHome "${HOME:?The user home directory is unavailable.}"
if (( $# == 2 )); then
    check_directory Results "$2"
fi

printf '\nPreflight passed. Provider tests still validate native identity and namespace behavior.\n'
