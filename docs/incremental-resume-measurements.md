# Incremental synchronization and resume measurements

The measured benefit is earlier useful work after interruption. Progress-enabled
runs reached the next previously uncommitted payload much sooner for ordinary
folders and independently stored child ZIPs. They still revalidated completed
work afterward, so total resume duration and total read-call counts stayed
similar. A single root ZIP retained its whole-package restart boundary.

Both settings use the same incremental implementation; the comparison disables
only retained progress. It measures journal-based scheduling, not the speedup
over the former global planner. Separate ordering tests establish that validated
folder writes can start before descendant discovery finishes.

These results cover the implementation at commit
`b96aaf3ee0330bb150d544edd26d0fa6e329c1f8` and the profiling harness delivered
with this report. The [compact results](incremental-resume-results.json) contain
all groups, method settings, raw-report hashes, and watchdog evidence. The
[runner instructions](../tools/Yabt.Profiling/README.md) describe reproduction.

## Method and validation

The measurement window was 2026-10-26, 10:00:51 through 10:13:22 UTC, on
CODEXSANDBOX01, the Hyper-V VM hosted by S650A. The Release workers used
.NET 10.0.13, Windows 10.0.26300, X64, eight logical processors, and workstation
GC. DSA agreed to avoid heavy work during the window. Watchdogs still observed
background CPU activity from Visual Studio, OneDrive, Codex, and other desktop
services; this was not whole-machine or hypervisor isolation.

There were 28 scenarios and 44 worker attempts, with 28 successful watchdog
records. Sixteen scenarios compared interrupted execution with progress enabled
or disabled. Twelve uninterrupted scenarios provided three repetitions per
setting for mirror backup and restore. All resumes completed, expected distinct
payload commit counts matched, and no previously committed payload path was
rewritten on resume. This is not an independent final byte comparison; integrity
checks remain part of production execution and the regression tests.
No progress-storage fallback warning occurred. Successful
commands removed their progress databases; interrupted enabled commands retained
one. The observer deduplicates same-attempt commit events, so this check does
not detect duplicate writes to a path within one attempt.

Fixtures contained 1,000 or 3,000 ordinary payload files of 32 bytes each, in
groups of 100 files per folder. Child-ZIP fixtures packaged each immediate
child; root-ZIP fixtures packaged the selected root. These cases stress metadata
small-file overhead, not large-payload streaming or network throughput.

Every attempt ran in a fresh process. Fixture creation and initial archive setup
were outside measured operation time. Workers performed a baseline GC without
warmup: first-use JIT is included, process startup is excluded, and filesystem
caches were not flushed. Restore concurrency was five. Payload storage retained
the 8 MiB spill threshold and shared 64 MiB budget. Progress used the default
32-unit or five-second flush boundary, checked at work boundaries.

Cancellation was requested after 550 payload commits for 1,000-file mirror and
restore fixtures, or 1,550 commits for 3,000-file fixtures. Child-ZIP backup
stopped after six package commits; root-ZIP backup stopped after its one package
commit. Package sidecars and ordinary control metadata do not count as payload
commits. Cooperative cancellation flushes safe pending progress on disposal.
Abrupt failure can lose the latest unflushed batch and require more replay; a
separate process-exit regression tests rollback of that batch.

Read and list counts come from structured provider invocation events. They
include control metadata and failed calls, not only payload access. They are not
byte counts, network round trips, or every native filesystem operation. Observer
path sets and commit-log output contribute to measured resource use.

## Restart to the next new payload

Each row below has one sample per setting. Times are seconds from starting the
resumed operation to committing a path absent from the first attempt's commit
log. Read counts cover provider object-read invocations before that commit.
These are observations supported by work counts, not statistical estimates.

| Case | Files | Off (s) | On (s) | Reads off/on |
| --- | ---: | ---: | ---: | ---: |
| Mirror backup | 1,000 | 4.375 | 0.862 | 1,103 / 103 |
| Mirror backup | 3,000 | 11.249 | 0.890 | 3,103 / 103 |
| Mirror restore | 1,000 | 4.515 | 1.647 | 558 / 58 |
| Mirror restore | 3,000 | 11.438 | 3.068 | 1,558 / 58 |
| Child-ZIP backup | 1,000 | 2.558 | 1.600 | 719 / 204 |
| Child-ZIP restore | 1,000 | 4.433 | 1.616 | 566 / 56 |
| Root-ZIP restore | 1,000 | 5.564 | 5.661 | 555 / 555 |

The 3,000-file mirror backup deferred 3,000 read calls until after the next new
commit; mirror restore deferred 1,500. The interrupted folder still required
comparison of its first 50 committed files. Progress therefore moved useful
work ahead of completed-folder revalidation without treating the journal as
authoritative content evidence.

Total resumed read calls were identical between progress settings in every
paired scenario. For the 3,000-file mirror backup, both settings issued 4,552
reads and took 27.349 versus 27.504 seconds overall. Restore issued 3,003 reads
and took 27.801 versus 27.807 seconds. The implementation improves how soon
resumed copying continues; these fixtures do not show reduced total recovery
work.

Child-ZIP backup still discovered and validated all package names in the parent
before writing. Its list-call count before the next new commit remained 44 with
either setting, while completed package comparisons moved later. Generic
artifact groups spread across folders retain conservative completion handling.

A root-ZIP backup resumed with no new payload path left: its package had already
committed, while its sidecar and final metadata were unfinished. It completed in
2.941 seconds without progress and 2.928 seconds with progress; next-new-payload
latency is correctly null. Root-ZIP restore also showed no prioritization
benefit because the incomplete package is one work unit. Neither case resumes
partial compression or maintains a cursor within package contents.

## Ordinary execution overhead

These uninterrupted 1,000-file mirror runs have three repetitions per setting.
Values are median seconds followed by the observed minimum-to-maximum range.
The delta compares medians; three repetitions do not establish a general
overhead bound or statistical significance.

| Operation | Progress off (s) | Progress on (s) | Median delta |
| --- | ---: | ---: | ---: |
| Backup | 11.593 [11.575, 11.622] | 11.814 [11.786, 11.999] | +1.9% |
| Restore | 10.842 [10.783, 10.920] | 10.804 [10.760, 11.704] | -0.3% |

Backup showed a small, consistent slowdown in these samples. Restore's median
was similar. Its third enabled run was slower at 11.704 seconds. The slightly
lower median is not evidence that progress makes normal restore faster. The
watchdog records background activity, but these runs do not isolate a cause for
that variation.

Median time to the first fresh payload was 0.539 versus 0.574 seconds for backup
and 1.092 versus 1.129 seconds for restore, with progress off versus on. This is
about 36-37 milliseconds of observed startup difference. No matching timing run
of the previous global-planning implementation was performed, so these values
are not a before-and-after speedup claim for the entire refactor.

## Memory and remaining cost

The following progress-enabled resume values use 250 ms sampling. Private and
managed values are observed process peaks; allocated memory is cumulative work
over the attempt. Units are MiB. Each row has one sample.

| Case | Files | Private peak | Managed peak | Allocated |
| --- | ---: | ---: | ---: | ---: |
| Mirror backup | 1,000 | 39.20 | 17.33 | 312.36 |
| Mirror backup | 3,000 | 52.40 | 19.09 | 937.73 |
| Mirror restore | 1,000 | 44.20 | 16.45 | 279.14 |
| Mirror restore | 3,000 | 54.27 | 18.52 | 877.70 |

Allocations still grow substantially with file count. Sampling can miss short
peaks, and managed peaks are not retained-memory measurements. These numbers
must not be compared directly with the forced-GC plan checkpoints in
[the earlier report](plan-memory-measurements.md). They establish neither a
whole-process memory bound nor asymptotic behavior from two fixture sizes.

Incremental restore now releases each folder/package plan after application.
Final logical-state evidence uses disk-backed desired membership, traversal,
duplicate detection, and output entries instead of retaining another complete
destination tree. Deterministic tests verify one final listing per folder and
no payload opening during that evidence collection. Wide folders, single large
packages, remaining operation caches, and metadata serialization still have
file-count-dependent costs.

Restore must still read and validate its root-wide live manifest before copying.
The journal cannot eliminate that startup work or current-source revalidation.
Further reductions in total restart work would need reusable freshness-checked
content evidence; reducing the root-manifest barrier would require a separate
index or durable-format design. Neither is implied by these measurements.

## Reproduce the summary

Raw reports and fixtures remain under `artifacts/incremental-resume`. Run:

```powershell
python tools/Yabt.Profiling/summarize-resume.py
```

The generator validates distinct commit counts and interruption/completion
outcomes, progress cleanup, environment consistency, and available watchdogs. It
stores `[median, minimum, maximum]` metrics and SHA-256 hashes of the source
reports in `docs/incremental-resume-results.json`. Single-sample groups have
identical values in all three positions. Safety limits and method caveats remain
part of interpreting the results, rather than pass/fail timing thresholds.
