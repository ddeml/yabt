# Synology CLI smoke checks

This small runner checks the published CLI against real native storage before
running broader provider and recovery acceptance on a Synology NAS. It creates
only synthetic data in a new caller-selected directory. It does not read live
shares, use Azure credentials, change NAS settings, or delete the test directory.

Run inside an ordinary-user Linux container with a prepublished .NET 10 CLI and
a dedicated Btrfs bind mount:

```bash
YABT_SOURCE_REVISION=<full-commit-id> \
  bash /tools/run-smoke.sh /app/Yabt.Cli.dll /work/smoke-001
```

The parent `/work` must exist and be writable. The selected child must not
exist, including as a symbolic link. The runner creates it with private
permissions and retains every fixture, command log, and result on success or
failure. Reuse requires a different child name. Keep the published CLI outside
the fixture directory and mount it read-only. Do not mount live NAS data or the
Docker socket. A single CPU and 2 GiB container memory are reasonable initial
limits; the synthetic files need less than 256 MiB plus logs and application
files. Runtime image compatibility is itself part of the check.

Required container tools are Bash, .NET 10, coreutils, GNU findutils, grep, and
`findmnt` from util-linux. The runner rejects root execution and filesystems
other than Btrfs. Developer validation may explicitly select ext4:

```bash
bash /tools/run-smoke.sh /app/Yabt.Cli.dll /work/dev-smoke-001 --allow-ext4
```

That records `ext4-developer-validation`, never Synology acceptance. Btrfs
success also needs the recorded kernel and external Docker/image evidence to
establish that execution occurred on the intended NAS. The existing
`tools/linux-acceptance` runner has a different ext4/Linux 5.8 baseline and must
not be relabeled as Synology acceptance.

The smoke sequence checks:

- Initial filesystem backup, native mutation controls and progress availability.
- An unchanged backup preserving live file identity, size and timestamps.
- Byte-for-byte archive verification and restore verification.
- Changed and deleted synthetic files, with original bytes preserved in history.
- A ZIP-policy subtree, Unicode/punctuation names, empty folders and exact file
  modification times restored through both mirror and ZIP representations.
- Disk-backed temporary content using a zero memory threshold/budget, with
  scratch, progress-journal and recovery-marker cleanup after successful work.

Runtime temporary files use `scratch/runtime`; YABT content and indexes use
`scratch/yabt`. Cleanup assertions cover YABT-owned scratch, since the runtime
may retain its own empty temporary directories. Console output stays concise;
complete command diagnostics remain in the per-command result logs.

Restore checks use fresh destinations so their complete-tree comparison does
not encounter intentionally retained destination history from an earlier run.
The runner does not test restore into an existing populated destination,
concurrent writers, forced termination, network mounts, directory links, ACLs,
hardware power loss, or performance. Follow it with the existing filesystem
contract, hard-link, permission and process-crash recovery tests on the NAS.

For those SDK-based `dotnet vstest` runs, also create a private, user-owned
directory with mode `0700` and export it as `XDG_RUNTIME_DIR`. A numeric
container UID need not have a passwd entry or home directory. Recent .NET
runtimes require a suitable runtime directory for named mutexes in that case;
`DOTNET_CLI_HOME` alone did not prevent the `NuGet-Migrations` startup failure
observed during NAS acceptance. See the
[upstream runtime discussion][runtime-directory].

Copy the prebuilt recovery-test assemblies into the owned writable test
workspace before running them. Their child `dotnet vstest` processes create
`TestResults` beside the assemblies. Keep the production CLI payload and the
container root filesystem read-only; only this disposable test copy needs to
be writable.

`results/environment.txt` records the kernel, UID, mounts, runtime, CLI SHA-256
and caller-supplied source revision. `cases.tsv` records completed cases, and
`run-status.txt` records the final phase and exit code. Require exit code zero,
phase `complete`, and all expected cases before accepting a run. A CLI warning
that local progress is unavailable fails the smoke check even if the command
otherwise succeeds. Each CLI command has a 120-second watchdog; an explicit
positive `YABT_SMOKE_COMMAND_TIMEOUT_SECONDS` overrides that limit.

Start acceptance containers with Docker `--init`, especially for the process
recovery tests. The test harness deliberately kills a process tree and then
waits for its descendant worker to disappear. An init process reaps orphaned
children; without it, an unreaped process can keep that wait pending. Retain
CPU and memory limits supported by the actual NAS. The inspected DS1817+
supports `--cpuset-cpus 0` and `--memory 2g`, but rejects CPU quota limits and
reports no PID-limit support. These are host-specific deployment observations,
not changes to the VM acceptance baseline.

[runtime-directory]: https://github.com/dotnet/runtime/issues/135726
