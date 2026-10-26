# Linux acceptance environment

This environment runs YABT's real .NET 10 test suite as an ordinary Linux user
inside Docker Desktop's Linux backend. It gives the project persistent Linux
storage and a reproducible SDK image while sharing the existing Docker engine.
The environment is dedicated to YABT; it does not reuse DSA containers, volumes,
networks, or credentials.

## Run from Windows

From the repository root in PowerShell:

```powershell
./tools/linux-acceptance/Invoke-LinuxAcceptance.ps1
```

The default `Test` action starts the environment and imports the requested Git
revision as a clean committed snapshot. Each run gets a fresh checkout and
result directory. Uncommitted edits and Windows `bin`/`obj` directories are not
copied into the Linux build. Select another commit with `-Revision <commit>`.

Other actions operate only on this Compose project:

```powershell
./tools/linux-acceptance/Invoke-LinuxAcceptance.ps1 -Action Start
./tools/linux-acceptance/Invoke-LinuxAcceptance.ps1 -Action Status
./tools/linux-acceptance/Invoke-LinuxAcceptance.ps1 -Action Stop
```

The host export is `artifacts/linux-acceptance/<run-id>`. It contains the Linux
environment record, build/test log, TRX results, and `acceptance-summary.json`.
Review the summary's failures and individual skip reasons. A successful test
process alone is insufficient if required Linux cases were skipped or missing.

## Storage and resource boundaries

Compose project `yabt-linux` uses one named volume, `yabt-linux-workspace`,
mounted once at `/work`. Its checkouts, test fixtures, temporary files, progress
journals, NuGet cache, and results remain available across container recreation.
The container can be rebuilt from its Dockerfile; the persistent volume carries
the reusable state. Stopping the environment preserves that volume.

The acceptance service has a two-CPU and 3 GiB memory limit. The initialization
service prepares the owned workspace directories; test execution uses UID
10001. No ports, Docker socket, or Windows source directories are mounted into
the acceptance container. Do not use system-wide Docker cleanup to maintain
this environment.

YABT checks native filesystem identity and directory semantics. The container's
OverlayFS writable layer and Windows host bind mounts do not provide the
supported baseline for these tests. Preflight requires the selected directories
to use ext2/ext3/ext4 or tmpfs and records the actual mount information. The
persistent named volume is expected to provide ext4; this is checked rather
than inferred from the fact that the process runs on Linux.

The container sets these paths outside each source checkout:

| Setting | Directory |
| --- | --- |
| `TMPDIR` | `/work/tmp` |
| `XDG_STATE_HOME` | `/work/state` |
| `NUGET_PACKAGES` | `/work/nuget` |
| `DOTNET_CLI_HOME` | `/work/cli-home` |
| User home directory | `/work/home` |

`TMPDIR` controls the real filesystem fixtures used by .NET's temporary-path
APIs. Linux tests run without root privileges so permission-denied behavior
remains observable. The SDK and test workers use Linux executables.

## Linux runner

The shell scripts also work in a conventional Linux environment with the same
prerequisites and explicitly configured writable storage directories:

```bash
bash tools/linux-acceptance/preflight.sh /path/to/yabt
YABT_SOURCE_REVISION=<full-commit-id> \
  bash tools/linux-acceptance/run-acceptance.sh /path/to/new-results
```

The source root comes from the runner's location. Its result directory must be
new and outside that checkout. Preflight records distribution, kernel, user,
SDK, and filesystem information, then checks Linux 5.8 or newer, a 64-bit
process environment, a .NET 10 Linux SDK, ordinary-user execution, and supported
storage. Provider tests still validate the actual native APIs and path rules.

The runner executes the full Release solution with one MSBuild worker and
disabled persistent build servers. It captures the test exit code without
letting `tee` hide a failure. A 30-minute watchdog bounds the complete build and
test command; `YABT_ACCEPTANCE_TIMEOUT_SECONDS` can set another positive limit.
After timeout, the command's process group receives termination and then a
forced kill after a 30-second grace period if necessary.

Logs and `run-status.txt` remain after failures. TRX documents retain test
outcomes and skip messages. `required-test-cases.tsv` records the acceptance
rows that the host summary checks: three Unix literal-backslash cases and all
seven forced-process recovery cases, plus three native Linux permission-denial
cases. Windows-specific skips are expected and are listed individually rather
than hidden by a total skipped count.

This is correctness acceptance, not a performance comparison with Windows.
Report a successful Docker Desktop run as Linux/ext4 acceptance under WSL 2.
It does not establish behavior for other filesystems, remote storage services,
different mount layouts, or hardware power loss. The same runner can later
provide a second platform check on a conventional Linux host.
