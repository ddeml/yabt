# Plan-memory measurements

Measured on 2026-10-23 against production commit `17aa2b3`. The diagnostic
runner is in [tools/Yabt.Profiling](../tools/Yabt.Profiling/README.md).
The production algorithms were unchanged during these measurements.
The final dataset contains 38 successful runs: 24 corrected retention
diagnostics and 14 normal command timings, plus three separate heap captures.

## Findings

Restore still retains state proportional to file count. Growth starts mainly
when building the desired-file plan. Reconciliation also creates transient
collections and retains classification/write state for execution. The
existing indexed archive enumeration retains comparatively little additional
managed memory. Releasing the operation owners releases the file-count-dependent
managed state and all observed scratch files.

Unchanged backup and restore timings provide no evidence of quadratic growth in
these fixtures. They do not establish complexity for other shapes or workloads.
Allocation churn remains substantial even when retained memory is modest.

## Method and scope

- Windows Hyper-V VM `CODEXSANDBOX01`, hosted on S650A; eight virtual processors,
  approximately 16 GiB RAM, AMD Ryzen 9 5950X reported to the guest.
- Windows build 26300, .NET 10.0.13, x64 Release, workstation GC, no debugger.
- Real filesystem source/archive/destination fixtures; deterministic 32-byte
  payloads, fixed-width names, fixed timestamps, flat mirror trees. A selected
  root packaged as ZIP provides a separate 10,000-file planning control.
- Payload counts exclude the policy and root descriptor. Each mirror plan
  contains `N + 1` files and projections because the policy is ordinary payload.
- Empty destinations contain no desired files. Unchanged destinations come from
  a successful restore. Mixed destinations have 10% changed, 10% missing, and
  10% extra files; 80% of payloads plus the policy remain unchanged.
- Fresh processes, logging disabled, planning warmup against a separate
  three-file fixture of the same format,
  restore concurrency five, payload threshold 8 MiB and shared budget 64 MiB.
- Fixture preparation is outside measurements. Normal restore repetitions use
  newly prepared destinations. Read-only planning repetitions reuse unchanged
  fixtures. Filesystem caches are not flushed.
- Normal public-command timing excludes startup, warmup, baseline capture, and
  report serialization. It includes the periodic sampler. Planning warmup does
  not warm the entire mutation pipeline.
- Retention diagnostics force a full GC at quiescent phase boundaries and keep
  active owners rooted. Each planning checkpoint awaits 100 ms before collection
  so producer completion stacks can unwind; the initial baseline also waits
  after warmup. This is practical stabilization, not a formal guarantee that
  every transient reference has disappeared. Their elapsed times are not
  performance results.
- A separate watchdog checks private memory and elapsed time every 250 ms.
  The private-memory threshold is 2 GiB. Normal runs allow ten minutes, reduced
  to five for 30,000 files. Final retained runs allow three minutes each;
  30,000-file preparation allows fifteen minutes. These are sampled thresholds,
  not exact quotas.
- Dsa Main Dev explicitly confirmed a bounded quiet window. No competing DSA,
  build, or test workload was observed in the initial batch. Ordinary desktop
  activity remained: up to 0.75 CPU seconds from other observed processes during
  a roughly 34-second normal restore, and 2.31 seconds during the roughly
  119-second restore. Endpoint observations cannot exclude
  short-lived processes, inaccessible processes, or host contention.

## Retained managed memory

Values are absolute MiB after collection, using the median of three runs against
unchanged destinations. MiB means 1,048,576 bytes.

| Phase | 1,000 files | 10,000 files | 30,000 files |
| --- | ---: | ---: | ---: |
| Baseline | 1.19 | 1.19 | 1.20 |
| Descriptors/live manifest | 1.39 | 1.39 | 1.36 |
| Archive enumerated | 1.40 | 1.39 | 1.25 |
| Restore plan built | 2.10 | 8.66 | 21.97 |
| Destination manifest read | 2.15 | 8.68 | 22.05 |
| Destination reconciled | 2.20 | 8.57 | 22.51 |
| Operation owners released | 1.37 | 0.95 | 0.81 |

Across the three destination scenarios, baseline-adjusted growth from 1,000 to
10,000 files was approximately **724-763 bytes per additional file** at the
planned checkpoint and **744-787 bytes** after reconciliation. Compute each
slope from the difference between medians of `(phase - baseline)`, divided by
9,000 additional payload files. These are aggregate observations, not exact
sizes assigned to individual collections.

The unchanged 1,000-to-30,000-file slope is about **718 bytes per file** after
planning and **734 bytes** after reconciliation. At 30,000 files, planned
memory ranged from 21.97 to 22.03 MiB, and reconciled memory from 22.5059 to
22.5104 MiB. This supports approximately linear retained growth in the measured
range; collection capacities and GC behavior make the slope inexact.

The small negative post-release slopes are runtime/GC variability, not negative
ownership. The 1,000-file diagnostics performed fourteen generation-2
collections after baseline; the 10,000-file diagnostics performed fifteen,
including an additional automatic collection during planning. Destination
semantics, transient call stacks, collection timing, and runtime state can affect
the aggregate values. Do not extrapolate a hard memory limit from these samples.

Mixed 10,000-file reconciliation retained a median 9.06 MiB; empty retained
8.62 MiB. Net change during reconciliation was about 0.48 MiB for mixed and
0.29 MiB for empty destinations. The unchanged case's median net change was
slightly negative, demonstrating that aggregate GC counters cannot assign an
exact byte cost to each owner. A retained endpoint does not include destination
dictionaries or temporary arrays that became unreachable before capture.

The 10,000-file ZIP control retained a median 10.95 MiB after planning and
10.91 MiB after unchanged reconciliation, compared with mirror's 8.66 and
8.57 MiB. It has two archive artifacts and one projection, but still restores
10,001 logical files. Fewer projection instances therefore do not imply less
total retained memory; the ZIP entry and opener ownership below also matters.
Its post-release median was 1.24 MiB.

## Whole-command timing and allocation

All commands below were successful unchanged operations. The 1,000- and
10,000-file results are medians of three fresh processes, with time ranges.

| Command | Files | Seconds, median [range] | Allocated MiB | Private MiB |
| --- | ---: | ---: | ---: | ---: |
| Backup | 1,000 | 1.420 [1.403-1.506] | 50.48 | 33.73 |
| Backup | 10,000 | 11.541 [11.515-11.600] | 565.09 | 54.30 |
| Backup | 30,000 | 32.973, single run | 1,787.11 | 76.32 |
| Restore | 1,000 | 3.722 [3.692-3.735] | 98.48 | 41.20 |
| Restore | 10,000 | 33.296 [33.029-33.416] | 1,073.45 | 64.82 |
| Restore | 30,000 | 118.762, single run | 3,341.51 | 98.20 |

`Private MiB` is the median sampled process-private peak. Allocations are
process-wide cumulative managed allocation deltas, including observers. They
are not simultaneous live memory. Private memory also includes native/runtime
allocations and must not be interpreted as managed ownership or SQLite memory.

For ten times as many files, median wall time grew 8.13 times for backup and
8.95 times for restore. Allocations grew 11.19 and 10.90 times respectively.
These observations support further investigation of approximately linear
working state and allocation churn; they do not prove general complexity.
The 30,000-file timings are single confirmation runs, not repeated estimates.

In the retained 10,000-file unchanged diagnostic, archive enumeration allocated
about 320 MiB, planning 151 MiB, and reconciliation 250 MiB. Enumeration's small
retained footprint therefore does not imply low allocation cost. Allocation
stack profiling is needed before attributing that churn to specific code.

## Scratch and ownership evidence

Mirror scratch logical file lengths at the enumeration-through-reconciliation
checkpoints were about 0.04 MiB for 1,000 files, 12.79 MiB for 10,000 files,
and 38.27 MiB for 30,000 files.
All released and finished checkpoints were zero. SQLite cache/journal behavior
means these sizes need not scale smoothly. They are not peak scratch use,
physical disk allocation, write volume, or bandwidth measurements.

Separate `dotnet-gcdump` 10.0.750501 snapshots at 10,000 files observed:

| Type | Mirror planned | Mirror reconciled | ZIP planned |
| --- | ---: | ---: | ---: |
| `ArchiveProjectedObject` | 20,002 | 20,002 | 10,003 |
| `ArchiveRestoreProjection` | 10,001 | 10,001 | 1 |

The mirror plan also retained 10,001 instances of each projection-collection
wrapper and a 420,480-byte dependency-dictionary entry array. ZIP retained
10,001 opener closures and 10,001 `ArchiveManifestEntry` objects. Source inspection
confirms that the ZIP opener captures the whole `entry` when it only needs
`entry.StoredPath`. These observations corroborate per-file object ownership;
the type totals do not assign exclusive retained bytes to an owning component.

The collector warned that .NET 10 uses potentially lossy EventPipe buffering,
so these are observed type counts rather than a completeness guarantee. The
binary snapshots and text reports remain local artifacts. Their total heap sizes
are not interchangeable with callback counters or dominator-tree ownership.

The snapshots also caught an observer error in the first diagnostic batch.
Without an awaited interval, a callback could run inside a producer's async
completion stack and keep temporary reconciliation dictionaries alive. A
10,000-file control dropped from roughly 12 MiB to 8.6 MiB after adding the
interval, consistent with the later paused snapshot. All retained samples were
repeated with the corrected method; superseded reports are preserved under
`artifacts/plan-memory/results/pre-quiescence` and excluded from aggregation.
Normal public-command timing was unaffected and was retained.

Source inspection identifies the ownership chain behind the plan-growth
hypothesis:

- `RestoreArchiveObjects` creates content-opening delegates/closures.
- `RestorePlanner` invokes mirror restore once per plain object, retains each
  returned projection, copies the final projected object, and stores its
  projection-dependency array in a path dictionary.
- `RestorePlan` owns the file list, projection list, and dependency dictionary
  through execution. Ordinary mirror projections have no disposable resource
  lifetime but still retain object/collection wrappers.
- `RestoreReconciler` builds desired/destination lookups, copied remaining-file
  state, write sets, and classification sets. Returned write arrays and
  classifications survive through execution; other dictionaries are transient.
- Execution adds projection-use counts and a write-path set. These are outside
  the diagnostic planning checkpoints but inside normal command measurements.
- Backup retains its desired-object snapshot, target-folder state dictionaries,
  and projection-consistency groups. Normal backup measurements cover the whole
  command; there is no phase-specific retained-heap attribution for backup yet.

## Recommended implementation sequence

1. Move restore reconciliation bookkeeping into operation-owned
   `TemporaryIndex` storage: destination records, consumed/classified paths,
   desired-path-to-plan-row lookup, and ordered writable row IDs. Preserve the
   existing plan/content owners initially. This is the smaller safe step and
   prepares stable identities for the larger plan migration. Its expected
   benefit is bounded transient bookkeeping, not the largest persistent-memory
   reduction; measure its transient/peak effects separately.
2. Replace per-file retained opener/dependency graphs with replayable metadata
   rows and explicit content references resolved by provider/format-owned
   content owners. Index dependency edges and remaining-use counts. Do not
   serialize delegates or retain an unbounded RAM delegate registry beside the
   disk index. Preserve started-worker draining before owner disposal.
   This is the main persistent-memory target supported by the measurements.
   Capture only the ZIP stored path in an opener when its other entry fields
   are unnecessary; that local reduction alone would not bound the full plan.
3. Re-run these fixtures, then profile allocation stacks and backup ownership
   before selecting the next target. Include grouped/deep trees, large payloads,
   nested packages, other providers, and native Linux in later acceptance work.

Step one must change membership to canonical path or stable row identity.
`HashSet<ArchiveProjectedObject>` currently relies on record equality, including
its opener delegate. Rehydrating a row with a new delegate would change equality
and could break removal/membership. Preserve provider comparison, blocker order,
deferred cleanup, cancellation, and scratch lifetime; add deterministic
work-count and recovery regressions rather than fragile timing thresholds.

## Reproduction and remaining limits

See the runner README for preparation, normal/retained commands, watchdogs, and
heap pauses. Full local reports, watchdog records, fixtures, and snapshots are
under ignored `artifacts/plan-memory`. The committed
[results](plan-memory-results.json) preserve aggregate values, raw-report
filenames and hashes, environment settings, and the aggregation script's hash.
The README documents regeneration with `summarize.py`; it rejects retained
reports from the superseded method. All 38 accepted runs have validated outcome
counts and zero scratch bytes at their released and finished checkpoints.

The Release solution suite passed 1,129 tests, with three existing Linux-only
skips. The final profiling-project build passed without warnings or errors.
The harness changes diagnostic access and adds tooling; no production algorithm
or memory-ownership change is included in this measurement work.

Sampling can miss short peaks. Forced-GC endpoints miss transient ownership.
Heap type totals are shallow sizes, not dominator/retained-owner sizes. These
measurements cover a bounded selection of filesystem cases and provide no total
process-memory guarantee. Provider-call counts and actual scratch I/O volume
were not measured here; the earlier deterministic scaling tests cover lookup
work separately. Native Linux and deployed-service acceptance remain pending.
