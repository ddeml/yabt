# Review after the GPT-5.6 Sol to GPT-6 Astra model change

## Purpose and baseline

This document preserves the findings discussed with Richard after the model change, for follow-up work in **Codex-Sandbox-01**.

- Original review: 2026-09-06 on S650A.
- Handoff documented: 2026-09-20.
- Reviewed baseline: commit `ca03f9ad21a7587cc6b15ce70f0440084ca87aa4` plus the then-uncommitted working tree, including the bounded restore pipeline, source-read failure handling, `verify-restore`, and Azure transfer/retry configuration. The handoff commit carries those pending changes alongside this document.
- Status: **all findings below remain open**. Recording and committing the baseline does not implement or approve the proposed fixes.
- Scope: correctness, archive safety, recovery, and scalability. This was a code review with focused experiments, not an exhaustive audit or an Azure service integration test.

References use repository-relative paths and method names so they remain useful in the VM. Proposals and acceptance checks are discussion starting points; follow the repository's current `AGENTS.md` when implementing them.

## Evidence and verification

The original review completed the existing test projects with **372 passed, 0 failed, and 1 skipped**. The skipped test was `GetFolderItemsAsyncDoesNotTraverseDirectoryReparsePoints`. Debug solution testing encountered files locked by the running CLI/Visual Studio session; the remaining checks were completed using a Release CLI test build and a separate sync test run.

Handoff verification on 2026-09-20: `dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal` completed successfully with **372 passed, 0 failed, and the same 1 skipped test**. These passing tests do not resolve the findings below.

The first four findings were reproduced through the built Release CLI with disposable filesystem fixtures. Backup conflict cases were also reproduced through that CLI. Policy exclusion behavior was reproduced against the built ZIP handler with an in-memory source. These ad hoc reproductions were not added as regression tests. Temporary fixtures on S650A are not part of this handoff and must be recreated in the VM.

Evidence labels distinguish runtime reproductions from static findings and design concerns. In particular, the cancellation race and verifier exception were not claimed as completed runtime reproductions in the review.

## Work list

| ID | Priority | Finding | Evidence | Status |
| --- | --- | --- | --- | --- |
| R01 | P1 | Path normalization silently omits source content | CLI reproduction | Open |
| R02 | P1 | Deduplication follows a linked ancestor outside the archive | CLI reproduction | Open |
| R03 | P1 | Missing source becomes a successful empty backup | CLI reproduction | Open |
| R04 | P1 | Backup accepts overlapping source and target roots | CLI reproduction | Open |
| R05 | P2 | Filesystem shape/casing changes prevent backup completion | CLI reproductions | Open |
| R06 | P2 | Folder-policy settings are accepted but ignored | Handler reproduction and inspection | Open |
| R07 | P2 | Unavailable restore-only entry can crash `verify-restore` | Static control-flow finding | Open |
| R08 | Investigate | Cancellation may release a lock before mutation finishes | Static concurrency concern | Open |
| I01 | Improvement | ZIP buffers accumulate across a backup | Lifetime inspection | Open |
| I02 | Improvement | ZIP restore repeatedly parses the central directory | Inspection and isolated probe | Open |
| I03 | Improvement | Current-live restore traverses history unnecessarily | Traversal inspection | Open |
| I04 | Improvement | Synchronizer responsibilities and provider test coverage | Architectural review | Open |

P1 means address before relying on the affected archival operation. P2 means a concrete correctness or usability defect. Investigate means the execution path is concerning but a controlled runtime reproduction is still needed.

## Priority findings

### R01: Path normalization can silently omit files

**Code:** [ArchiveLayout.cs](../src/Yabt.Core/Models/ArchiveLayout.cs), `NormalizeObjectKey`; filesystem listing and projection traversal consume the normalized keys.

**Reproduction:** Create a source folder named `" documents"` with a leading space and put `important.txt` inside it. Use the default mirror policy and a separate filesystem target. Run backup. It exits successfully but archives only `documents/.yabt-empty`; the source file is omitted.

**Cause and impact:** `StringSplitOptions.TrimEntries` changes each path component. Subsequent traversal visits `documents` rather than the actual ` documents` directory. Missing-directory handling then makes it appear empty. Normalization can also alias distinct native names.

**Proposed direction:** Preserve object identity exactly. Separate configuration cleanup from object-key validation. Reject names a provider cannot represent instead of silently rewriting them.

**Acceptance checks:** Round-trip leading-space names and distinguish spaced/unspaced siblings. Cover other platform-representable whitespace names and explicitly unsupported names. Assert both payload and restored path identity; a successful backup must not replace a nonempty source directory with an empty marker.

### R02: Deduplication can mutate files outside the archive

**Code:** [FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs), `GetFolderItemsAsync`, `ResolveObjectPath`, and guarded mutations.

**Reproduction:** Under a unique temporary fixture, create sibling `archive` and `outside` directories. Make `archive/linked` a junction to `outside`. Set the archive descriptor's `histPrefix` to `linked/history`. Put two identical 20 KB historical files under `outside/history` in a timestamp occurrence directory. Run `deduplicate`. It exits successfully, deletes one external duplicate, and writes reference/catalog metadata outside the configured archive root.

**Cause and impact:** Listing checks the final directory for reparse attributes, but that directory can be ordinary while an ancestor is linked. Path resolution validates lexical containment only. Destructive maintenance consequently escapes the configured root.

**Proposed direction:** Validate every existing descendant path component consistently for listing, reading, writing, moving, deleting, locking, and staging. Preserve the documented ability to configure a linked directory as the store root itself.

**Acceptance checks:** Test direct and nested linked ancestors in live, history, and `.yabt-tmp` paths. All external fixture contents must remain unchanged after a refused operation. Exercise junctions as well as symbolic links where available; ensure the important Windows case does not disappear behind a skipped test.

### R03: A nonexistent source can become a successful empty backup

**Code:** [ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs), `CreateContextAsync`; [JsonBackupRootLocator.cs](../src/Yabt.Metadata/Implementation/JsonBackupRootLocator.cs), `LocateRootAsync`; [FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs), `GetFolderItemsAsync`.

**Reproduction:** Create a valid source root descriptor and a separate archive containing `important.txt`. Invoke backup on a nonexistent child of the source root, such as `source/typo-nonexistent`. The locator finds the parent's descriptor. Backup exits successfully, moves the archive file into history, and publishes an empty-folder marker and live manifest. The requested source directory still does not exist.

**Cause and impact:** An unavailable source is treated as an empty listing. Source readiness and target initialization are insufficiently distinguished. Existing bytes survive in history in this reproduction, but the live archive is incorrectly reconciled to an empty state.

**Proposed direction:** Refuse a missing or inaccessible selected source before archive mutation. Define how source disappearance during traversal differs from a legitimately empty directory; do not infer emptiness from failed availability checks.

**Acceptance checks:** Missing selected roots, missing configured live prefixes, inaccessible sources, and disappearance during traversal must fail without publishing a successful empty state or performing unrelated removals. A genuinely empty source must remain supported.

### R04: Backup accepts overlapping source and target roots

**Code:** [ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs), `CreateContextAsync` and `ApplyProjectionAsync`; compare existing restore location validation.

**Reproduction:** Configure a source descriptor whose filesystem target is the source directory itself. Add a ZIP policy and `original.txt`. Run backup. It succeeds, installs ZIP artifacts in that directory, and moves the original file and policy into its own history.

**Cause and impact:** Backup has no physical source/target overlap guard. Source files become obsolete target objects during reconciliation, so a backup unexpectedly mutates its input.

**Proposed direction:** Reject identical and ancestor/descendant filesystem roots after resolving physical aliases. Share the relevant path policy with restore and logging validation rather than implementing another inconsistent check.

**Acceptance checks:** Equal roots, target under source, source under target, and junction aliases must fail before mutation. Separate roots must continue to work.

## Further correctness findings

### R05: Filesystem shape and casing changes prevent backup completion

**Code:** [ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs), `TakeExistingBackupObjectAsync`, `ProcessBackupObjectAsync`, and the later extra-item historization.

**Reproductions:** A desired source file `item` conflicts with an existing archive directory `item/old.txt`. Separately, on Windows, source `Report.txt` conflicts with archive `report.txt`. Both backups fail during upload. Existing archive bytes survive, but rerunning does not resolve either conflict.

**Proposed direction:** Classify filesystem representation conflicts before upload, validate/stage the replacement, and historize its blocker before committing. Preserve complete old folders, including native empty descendants and `.yabt-empty` markers. Case comparison must reflect the target filesystem's representational capabilities.

**Acceptance checks:** Test directory-to-file, file-to-directory, case-only file and folder changes, failed preparation, and successful reruns. The first direction and case-only filename case were reproduced; the remaining cases are proposed regression coverage.

### R06: Policy settings are accepted but ignored

**Code:** [FolderPolicy.cs](../src/Yabt.Core/Models/FolderPolicy.cs), [MirrorArchiveFormatHandler.cs](../src/Yabt.Format.Mirror/Implementation/MirrorArchiveFormatHandler.cs), and [ZipArchiveFormatHandler.cs](../src/Yabt.Format.Zip/Implementation/ZipArchiveFormatHandler.cs), `ProjectBackupAsync`.

**Reproduction:** Give the ZIP handler an in-memory source containing `secret.env` and a policy with `excludePatterns = ["**/secret.env"]`. The generated ZIP still contains that file.

**Inspection:** Include/exclude patterns and folder options are stored or serialized but are not applied to projection. ZIP compression uses global options. The policy documentation presents a draft shape containing these fields, so whether to implement them now is a design decision; silently accepting ineffective settings is the immediate problem.

**Proposed direction:** Either define and implement the semantics or reject nonempty unsupported settings. Also consider rejecting unknown policy properties so misspelled settings do not silently succeed.

**Acceptance checks:** Verify the chosen behavior for mirror and ZIP, nested policies, exclusions, and per-folder compression. Do not silently archive a file that the accepted policy says to exclude.

### R07: `verify-restore` can dereference an unavailable entry

**Code:** [RestorePathVerifier.cs](../src/Yabt.Sync/Implementation/RestorePathVerifier.cs), `CompareDirectoryAsync` and `EnumerateEntries`.

**Static finding:** The union of names includes `restoreEntries.UnavailableNames`. A restore-only junction or attribute-read failure can therefore leave both entry lookups empty. The source-missing branch increments `RestoreOnlyCount` and dereferences `restoreEntry` when Information logging is enabled. Without that logging, it still misclassifies the unavailable item. Existing tests using `NullLogger` miss the exception path.

**Proposed direction:** Handle unavailable names from either side before classifying source-only or restore-only entries.

**Acceptance checks:** Use an Information-enabled logger and restore-only inaccessible/reparse entries. Verification must report uncompared entries, return a nonidentical result, and avoid throwing or double-counting.

### R08: Cancellation may release ownership before mutation finishes

**Code:** [FileSystemRestoreTarget.cs](../src/Yabt.Sync/Implementation/FileSystemRestoreTarget.cs), `CommitStagedFileAsync`; [FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs), move operations; [YabtTaskExtensions.cs](../src/Yabt.Common/Async/Extensions/YabtTaskExtensions.cs), `WaitOrAbandonAsync`.

**Static concern, not a reproduced race:** `YabtTask.Run` intentionally cancels waiting while synchronous work can continue. A commit or move may still be active when callers clean staging and dispose the archive lock. A subsequent command could acquire that lock before the previous mutation finishes, particularly with delayed filesystem I/O.

**Proposed direction:** Once an irreversible filesystem step starts, retain its staging and lock ownership until it finishes, then observe cancellation at the next safe boundary. Observing an abandoned exception alone does not serialize the mutation.

**Acceptance checks:** Add a controllably delayed mutation, cancel it, and attempt a second writer. Assert that cleanup and lock release cannot overtake the first mutation. Preserve cancellation responsiveness during preparation and between commits.

## Improvement opportunities

### I01: Bound ZIP memory over the complete backup

[ArchiveProjectionObjectStore.cs](../src/Yabt.Sync/Implementation/ArchiveProjectionObjectStore.cs) retains projected objects/scopes. Their delegates retain `ZipPackageProjectionMaterialization` in [ZipArchiveFormatHandler.cs](../src/Yabt.Format.Zip/Implementation/ZipArchiveFormatHandler.cs), whose completed task retains the package byte array. Many independent ZIP children therefore retain their combined compressed size even after upload.

Introduce explicit materialization ownership and release after the complete consistency group is processed. Preserve sibling revalidation and source-read-once guarantees. Consider spillable package construction separately. Measure peak memory over many packages; the known in-memory ZIP design is a limitation, and cumulative retention makes it broader than a per-package limit.

### I02: Avoid repeated ZIP central-directory parsing

[StagedZipRestorePackage.cs](../src/Yabt.Format.Zip/Implementation/StagedZipRestorePackage.cs), `OpenEntryAsync`, opens a fresh `ZipArchive` and calls `GetEntry` for each output. Restoring N entries consequently repeats central-directory parsing with quadratic aggregate work.

An isolated .NET 10 in-memory probe matching this pattern counted approximately 2.1/6.6/22.9 MB of reads for 100/200/400 one-byte entries, from ZIPs of only 10.9/21.8/43.6 KB. This corroborates the access-pattern cost; it is not an end-to-end restore benchmark.

Consider a bounded pool of reusable readers per staged package, with explicit ownership for concurrent extraction. Measure allocations and restore time while preserving full package validation and concurrency bounds.

### I03: Prune history before current-live restore traversal

[ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs), `LoadRestoreObjectsAsync`, recursively lists the live prefix and filters internal paths afterward. With the default empty live prefix, this enumerates history and staging descendants unnecessarily. Restore work grows with accumulated history; inaccessible history can also interfere with a current-live restore.

Use folder-local traversal and prune reserved prefixes before descending. Verify provider call counts with a small live tree and large history tree. Evaluate destination safety traversal separately: its deliberate safety checks must not be removed merely to reduce scanning.

### I04: Separate orchestration responsibilities and strengthen provider tests

`ArchiveSynchronizer` is approaching 6,000 lines. Useful boundaries include backup execution, restore planning/execution, and manifest transactions. Shared filesystem identity, containment, and availability rules would address the recurring assumptions behind R01-R05.

Refactor after targeted regressions establish behavior. Prefer cohesive components with ownership of real invariants over additional forwarding abstractions. Add provider contract tests and controlled failures around hierarchy, moves, conditional mutations, and lock lifetime; the Azure provider tests at review time mostly exercised configuration and client construction rather than actual storage operations.

Related observations to evaluate during this work:

- Restore scans every planned history move before every committed file; index blockers by path/ancestor to avoid O(writes x history moves) checks.
- Restore path comparison is selected by operating system rather than actual filesystem case sensitivity. Include case-insensitive non-Windows volumes and case-sensitive Windows directories when portability work expands.

## Suggested Codex-Sandbox-01 sequence

1. Check out the handoff commit and read `AGENTS.md` plus this document. Recreate fixtures using VM-local paths; the committed launch profiles contain S650A-specific paths and are not VM setup instructions.
2. Establish the test baseline with `dotnet test yabt.sln --configuration Release --verbosity minimal`.
3. Add targeted regression tests for R01-R04, confirm failures, and address those safety findings first. Use only disposable data for the destructive reproductions.
4. Resolve R05-R07 and agree on R06 policy semantics. Investigate R08 with deterministic coordination rather than timing-dependent sleeps.
5. Measure I01-I03, then refactor under the resulting tests and provider contracts.
6. Update each finding's status with the fixing commit and verification evidence. Do not mark an item resolved merely because the pre-existing suite passes.

The findings are a review handoff, not a blanket redesign specification. Keep fixes individually reviewable and discuss changes to durable formats, policy semantics, or architectural requirements with Richard.
