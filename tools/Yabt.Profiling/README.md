# YABT memory profiling

This diagnostic runner measures real filesystem fixtures without retaining a
second in-memory copy of the source tree. It uses production backup, restore,
planning, and reconciliation components. It is intended for local investigation;
elapsed-time and memory measurements are not unit-test pass/fail thresholds.

Build once in Release before collecting measurements. Run the compiled DLL in a
fresh process for every sample, with no debugger attached. Keep concurrent builds,
tests, applications, and VM-host load quiet. Coordinate a bounded quiet window
when other development work shares the machine or its host.

## Prepare fixtures outside measured processes

The positional command forms are:

```text
prepare <fixture> <file-count> <flat|grouped> <mirror|zip>
destination <fixture> <empty|unchanged|mixed> <new-name>
run <fixture> <destination-name> <plan|restore|backup>
    <normal|retained> <new-report.json> [warmup-fixture] [pause-phase]
```

The last command is shown on two lines for readability. Supply its arguments as
one command. Parent directories must already exist; fixture roots, destination
names, and report files must be new. The runner does not recursively delete or
reset existing fixtures. Failed preparation leaves its owned partial fixture in
place, with a marker that prevents treating it as ready.

From the repository root, this PowerShell example prepares separate fixture and
report locations:

```powershell
dotnet build tools/Yabt.Profiling/Yabt.Profiling.csproj -c Release
$runner = 'tools/Yabt.Profiling/bin/Release/net10.0/Yabt.Profiling.dll'
New-Item -ItemType Directory artifacts/profiling-fixtures -Force
New-Item -ItemType Directory artifacts/profiling-reports -Force
$fixture = 'artifacts/profiling-fixtures/mirror-flat-1000'
$warmup = 'artifacts/profiling-fixtures/warmup-mirror-flat'

dotnet $runner prepare $fixture 1000 flat mirror
dotnet $runner destination $fixture empty empty-01
dotnet $runner destination $fixture unchanged unchanged-01
dotnet $runner destination $fixture mixed mixed-01

dotnet $runner prepare $warmup 10 flat mirror
dotnet $runner destination $warmup unchanged unchanged
```

Each source contains the requested number of deterministic 32-byte payloads,
fixed-width names, and fixed UTC file timestamps. The count excludes the source
policy, root descriptor, and generated archive metadata. `flat` puts payloads in
one folder; `grouped` uses 100 payloads per immediate child folder. `zip` applies
the ZIP policy at the selected source root, producing one root package.

Archive preparation performs a real backup and marks the fixture ready only
after success. Destination preparation behaves as follows:

- `empty` creates an existing empty destination directory.
- `unchanged` performs a real restore, retaining its logical-state manifest.
- `mixed` starts with that restore, then changes every tenth payload, removes the
  next payload in each group of ten, and adds one extra file per group. Changed
  payloads retain their length but have different bytes and timestamps. The old
  logical-state manifest remains so reconciliation must detect these changes.

Destination preparation has no separate readiness marker. If it fails or is
interrupted, its directory may contain partial state that `run` will accept.
Use only destinations whose preparation completed successfully. Prepare a new
destination name after a failure; do not reuse the partial destination as a
measurement baseline.

For counts divisible by ten, mixed destinations contain 10% changed, 10% missing,
and 10% extra payloads. Small counts round according to the deterministic
zero-based index rules. The preparer uses a bounded ownership marker and refuses
linked fixture root directories. It does not hash the whole fixture on every
load or protect against external changes during a run; keep fixtures untouched.

## Measure one workload per process

For retained restore-plan memory:

```powershell
$report = 'artifacts/profiling-reports/flat-1000-mixed-retained-01.json'
dotnet $runner run $fixture mixed-01 plan retained $report $warmup
```

For a complete mutating restore:

```powershell
$report = 'artifacts/profiling-reports/flat-1000-empty-restore-01.json'
dotnet $runner run $fixture empty-01 restore normal $report $warmup
```

Use another newly prepared destination for each mutating restore repetition.
Otherwise an empty or mixed case becomes an unchanged case. Read-only `plan`
runs can reuse one destination when no other command changes it. Run preparation
and measurement sequentially, and repeat cases in fresh processes. Vary file
count, tree shape, and archive format independently.

The optional warmup fixture must contain at most ten payloads. Prepare its
destination with the exact name `unchanged`; warmup performs read-only restore
planning against that path before sampling. Choose the same format and shape as
the measured fixture when possible. Warmup owners are released and collected
before baseline measurement. This warms planning code; it does not exercise the
complete restore-write or backup-write pipeline.

`backup normal` invokes the public backup command against the prepared source
and archive. The destination-name argument is still required and must identify
an existing prepared destination, although backup does not use that destination.
Because preparation already backed up the source, this initially measures an
unchanged backup. There is no built-in changed-source backup scenario.

Reports must be new files outside the measured fixture tree, in an existing
parent directory. Keep them outside warmup fixtures as well. JSON reports include
the workload, runtime and machine information, fixed configuration, counters,
checkpoints, result, and any captured operation failure. A nonzero exit code or
non-null failure invalidates the sample as a successful workload measurement.

## Separate timing from retention diagnostics

`restore normal` and `backup normal` execute complete public commands. Their
wall-clock duration excludes process startup, fixture loading, warmup, baseline
capture, and report serialization. Sampling still adds overhead. Only successful
normal whole-command reports set `timingComparable` to true.

`plan` calls the production restore planning components through a diagnostic
driver. It validates and reads the archive and destination without applying a
restore. It creates disposable scratch indexes and captures these phases:

1. `baseline`: services exist; the measured operation has not started.
2. `prepared`: source/archive descriptors and live manifest are available.
3. `enumerated`: current archive objects have been enumerated.
4. `planned`: expanded projections and desired namespace have been validated.
5. `destination-manifest`: destination logical-state evidence has been loaded.
6. `reconciled`: destination enumeration and comparison have completed.
7. `released`: the owning planning method has returned and disposed its owners.
8. `finished`: the measured call has ended.

Destination enumeration is part of reconciliation and is not repeated to create
another checkpoint. `retained` mode forces a full collection, waits for pending
finalizers, and collects again at each checkpoint. It is accepted only for
`plan`. Before each retained planning callback captures its checkpoint, it awaits
100 ms so asynchronous producer completion stacks can unwind. The same delay
precedes the initial retained baseline after warmup. This provides practical
quiescence, not a strict guarantee that all transient references have vanished.
Owners needed by the active phase remain explicitly rooted through its callback.

Without this delay, an inline completion callback can retain producer stack
locals that are no longer part of the completed phase's intended ownership.
Those references can inflate the apparent retained plan size even after a full
collection. Reports record `retainedCheckpointDelayMilliseconds` so aggregation
can reject the earlier retained methodology. Compare independent heap snapshots
when exact ownership matters; the delay itself does not establish attribution.

Normal planning captures the same phases without forced collections. Both
planning modes have diagnostic overhead and their phase timings are not
whole-command performance results. Normal public backup and restore timing does
not use the retained checkpoint delay.

For external heap inspection, add a warmup fixture and a pause phase:

```powershell
$report = 'artifacts/profiling-reports/flat-1000-mixed-heap-01.json'
dotnet $runner run $fixture mixed-01 plan retained $report $warmup reconciled
```

The runner writes `<report>.checkpoint.json` containing the process ID, phase,
and resume-file path. Attach the chosen .NET heap diagnostic tool to that process,
then create the indicated `<report>.resume` file to continue. Keep snapshot output
outside all fixtures. Paused runs remain subject to the time budget and must not
be used for elapsed-time comparisons. The report and its sidecar names must all
be unused before the run starts.

## Read the measurements with their limits

Checkpoint values are cumulative unless their field explicitly says otherwise.
Subtract baseline allocation, CPU-time, and GC counts to obtain operation deltas.
Compare retained `managedBytes` across file counts and phases to estimate bytes
retained per additional payload. Inspect heap snapshots to attribute growth to
specific owners; aggregate counters cannot establish ownership by themselves.

| Field | Meaning and limitation |
| --- | --- |
| `managedBytes` | Managed memory estimate; retention evidence only after GC. |
| `totalAllocatedBytes` | Process-wide managed allocations, including observers. |
| GC heap fields | Heap, fragmentation, and commitment at the last collection. |
| `privateBytes` | Process-private memory, including runtime/native allocations. |
| `workingSetBytes` | Resident memory; depends on OS and host memory pressure. |
| `processCpuMilliseconds` | Total CPU time, accumulated across process threads. |
| `scratchLogicalBytes` | Scratch file lengths at that checkpoint, not disk usage. |
| Sampled peaks | Maxima observed at checkpoints and every 250 milliseconds. |

Sampled peaks can miss shorter spikes. Scratch size is neither peak scratch use
nor bytes physically allocated on disk and is not an I/O throughput counter.
The sampler retains only aggregate peaks and a bounded set of phase checkpoints,
but its allocations and process inspection remain part of the observed process.
Normal mode starts after one baseline collection; it does not represent a cold
process or cold filesystem cache. File caches are not flushed between runs.

The runner fixes restore preparation concurrency at five and retains the default
8 MiB payload threshold and 64 MiB shared payload memory budget. SQLite and other
native memory are reflected in process metrics, not managed allocations alone.
An elevated post-release working set does not by itself establish a managed leak.
Restoring tiny files isolates metadata scaling but does not cover large payloads,
deeply nested packages, other providers, or every possible directory shape.

## Safety budgets

Two positive-integer environment settings bound diagnostic runs:

| Setting | Default | Scope |
| --- | ---: | --- |
| `YABT_PROFILE_MAX_SECONDS` | 600 | Entire command, including warmup and pauses. |
| `YABT_PROFILE_MAX_PRIVATE_MIB` | 2048 | Private-memory samples during `run`. |

Set these in the runner's environment before launching a process. They request
cooperative cancellation; they are not hard process, memory, or disk quotas.
Started filesystem mutation workers may finish before cancellation returns, and
memory can grow between samples. The private-memory limit does not cover fixture
preparation or warmup. Increase workload sizes only after checking the preceding
case's time, memory, and available disk space. Interrupted preparation or restore
may leave owned partial state for inspection; no automatic cleanup is performed.

For unattended preparation and measurement, use the external watchdog:

```powershell
./tools/Yabt.Profiling/Invoke-Profile.ps1 `
  -RecordPath artifacts/profiling-reports/run-01.watchdog.json `
  -TimeoutSeconds 600 -PrivateLimitMiB 2048 `
  -RunnerArguments @('run', $fixture, 'unchanged-01', 'restore',
    'normal', 'artifacts/profiling-reports/run-01.json', $warmup)
```

It launches one child with redirected output and no visible window, applies the
memory/time checks to startup and preparation as well, and terminates only that
child process tree if a limit is exceeded. Samples occur every 250 ms, so these
are watchdog thresholds rather than exact allocation quotas. The child's own
timeout requests cooperative cancellation five seconds before the watchdog.
A killed command can leave an incomplete fixture; do not reuse it as a baseline.

The watchdog records other observable processes that existed at both endpoints
and accumulated at least 0.1 CPU seconds. This can reveal competing guest work,
but misses short-lived processes, inaccessible processes, and hypervisor-host
activity. A quiet-window agreement and repeatability checks remain necessary.

## Aggregate completed runs

`summarize.py` uses Python 3.10 or later and only the standard library. Run it
once all measurements have completed:

```powershell
python tools/Yabt.Profiling/summarize.py
```

The defaults read `artifacts/plan-memory/results` and replace
`docs/plan-memory-results.json`. Use `--input` and `--output` to choose other
paths. Inputs must have matching watchdog reports and names in this form,
shown across two lines for readability:

```text
<flat|zip>-<count>-<empty|unchanged|mixed>-
<retained|restore|backup>-<repeat>.json
```

The generator validates successful runs, expected counts for flat mirror/ZIP
fixtures, matching builds and runtime settings, and released scratch storage.
Retained reports must record a 100 ms checkpoint delay; earlier retained reports
are rejected. Normal reports may omit that field, which means zero. The summary
records the delay per group rather than requiring it to match across modes.
Grouped fixtures require extending the expected-count checks first. It excludes
heap captures, checkpoint sidecars, preparation, and smoke runs. The summary
records source hashes and `[median, minimum, maximum]` statistics; diagnostic
wall-clock timings are omitted. Keep the raw reports to reproduce the summary.
