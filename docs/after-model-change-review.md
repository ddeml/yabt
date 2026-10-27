# Review after the GPT-5.6 Sol to GPT-6 Astra model change

## Purpose and baseline

This document preserves the findings discussed with Richard after the model
change, for follow-up work in **Codex-Sandbox-01**.

- Original review: 2026-09-06 on S650A.
- Handoff documented: 2026-09-20.
- Reviewed baseline: commit `ca03f9ad21a7587cc6b15ce70f0440084ca87aa4` plus the
  then-uncommitted working tree, including the bounded restore pipeline,
  source-read failure handling, `verify-restore` , and Azure transfer/retry
  configuration. The handoff commit carries those pending changes alongside this
  document.
- Status: **R01-R09 and I01-I03 are implemented for their documented scopes;
  I04 implementation, local contracts, Linux/ext4 acceptance, and deployed Azure
  acceptance are complete; unresolved WebDAV and pCloud work is backlogged**.
  Recording the baseline did not
  implement the fixes. R09 was added during the follow-up discussion on
  2026-09-21.
- Scope: correctness, archive safety, recovery, and scalability. This was a code
  review with focused experiments, not an exhaustive audit or an Azure service
  integration test.

References use repository-relative paths and method names so they remain useful
in the VM. Proposals and acceptance checks are discussion starting points;
follow the repository's current `AGENTS.md` when implementing them.

## Evidence and verification

The original review completed the existing test projects with **372 passed, 0
failed, and 1 skipped**. The skipped test was
`GetFolderItemsAsyncDoesNotTraverseDirectoryReparsePoints` . Debug solution
testing encountered files locked by the running CLI/Visual Studio session; the
remaining checks were completed using a Release CLI test build and a separate
sync test run.

Handoff verification on 2026-09-20:
`dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal`
completed successfully with **372 passed, 0 failed, and the same 1 skipped
test**. These passing tests do not resolve the findings below.

The first four findings were reproduced through the built Release CLI with
disposable filesystem fixtures. Backup conflict cases were also reproduced
through that CLI. Policy exclusion behavior was reproduced against the built ZIP
handler with an in-memory source. These ad hoc reproductions were not added as
regression tests. Temporary fixtures on S650A are not part of this handoff and
must be recreated in the VM.

Evidence labels distinguish runtime reproductions from static findings and
design concerns. In particular, the cancellation race and verifier exception
were not claimed as completed runtime reproductions in the review.

## Work list

| ID | Priority | Finding | Evidence | Status |
| --- | --- | --- | --- | --- |
| R01 | P1 | Path normalization silently omits source content | CLI reproduction and exact-path regression tests | Implemented |
| R02 | P1 | Deduplication follows a linked ancestor outside the archive | CLI reproduction and junction regression tests | Implemented |
| R03 | P1 | Missing source becomes a successful empty backup | CLI reproduction and source-readiness regression tests | Implemented |
| R04 | P1 | Backup accepts overlapping source and target roots | CLI reproduction and overlap/recovery regression tests | Implemented (best effort) |
| R05 | P2 | Filesystem shape/casing changes prevent backup completion | CLI reproductions and transition/recovery regression tests | Implemented |
| R06 | P2 | Folder-policy settings are accepted but ignored | Warning, validation, and round-trip regression tests | Implemented (warn and ignore) |
| R07 | P2 | Unavailable restore-only entry can crash `verify-restore` | CLI reproduction and unavailable-entry regression tests | Implemented |
| R08 | P1 | Cancellation releases a lock before mutation finishes | Gated filesystem mutation reproduction and ownership regression tests | Implemented |
| R09 | P1 | Hard-linked archive files share mutable contents and distort deduplication savings | CLI reproduction and hard-link/recovery regression tests | Implemented |
| I01 | Improvement | ZIP buffers accumulate across a backup | Generated large-stream and projection-lifetime regression tests | Implemented |
| I02 | Improvement | ZIP restore repeatedly parses the central directory | Entry-read instrumentation and large-manifest round-trip tests | Implemented |
| I03 | Improvement | Current-live restore traverses history unnecessarily | Traversal-count and unreadable-subtree regression tests | Implemented |
| I04 | Improvement | Synchronizer responsibilities and provider test coverage | Component extraction, provider contracts, recovery fault tests, Linux/ext4 and deployed Azure acceptance | Implemented for accepted scopes; WebDAV/pCloud acceptance backlogged |

P1 means address before relying on the affected archival operation. P2 means a
concrete correctness or usability defect. Investigate means the execution path
is concerning but a controlled runtime reproduction is still needed.

## Priority findings

### R01: Path normalization can silently omit files

**Code:** [ArchiveLayout.cs](../src/Yabt.Core/Models/ArchiveLayout.cs) ,
`NormalizeObjectKey` ; filesystem listing and projection traversal consume the
normalized keys.

**Reproduction:** Create a source folder named `" documents"` with a leading
space and put `important.txt` inside it. Use the default mirror policy and a
separate filesystem target. Run backup. It exits successfully but archives only
`documents/.yabt-empty` ; the source file is omitted.

**Cause and impact:** `StringSplitOptions.TrimEntries` changes each path
component. Subsequent traversal visits `documents` rather than the actual
` documents` directory. Missing-directory handling then makes it appear empty.
Normalization can also alias distinct native names.

**Proposed direction:** Preserve object identity exactly. Separate configuration
cleanup from object-key validation. Reject names a provider cannot represent
instead of silently rewriting them.

**Acceptance checks:** Round-trip leading-space names and distinguish
spaced/unspaced siblings. Cover other platform-representable whitespace names
and explicitly unsupported names. Assert both payload and restored path
identity; a successful backup must not replace a nonempty source directory with
an empty marker.

**Implementation on 2026-09-27:** Shared key validation preserves exact name
components, including whitespace-only names. Only null and empty input mean the
root. Empty segments, rooted/drive-prefixed paths, `.`/`..`, and NUL are refused
instead of collapsed or rewritten. The initial implementation accepted explicit
backslash separators; the provider-boundary refactor below replaces that shared
conversion. Native Unix filenames containing a literal backslash are refused
before they can select a different branch.

Filesystem listing and all key-based operations preserve whitespace and reject
Windows aliases such as trailing spaces/dots and reserved device names. Restore
checks both planned paths and existing destination names before mutation. Native
names are checked before Windows full/relative-path conversion can erase their
spelling, including when selecting a source folder directly.
Azure names and WebDAV URI components retain exact identity, with malformed or
ambiguous provider names refused. Policies, every manifest kind, ZIP entries,
and package naming preserve exact supported names; schemas follow the same
canonical path contract.

Regression coverage includes mirror/ZIP backup, repeat backup, byte-for-byte
verification, restore, directly selected spaced folders with an unspaced decoy,
distinct spaced/unspaced siblings, whitespace-only and Unicode whitespace names,
native empty folders, guarded provider mutations, and destination refusal with
existing contents unchanged. Linux-only native-name cases require a Linux test
run; the Windows suite exercises real extended-path trailing-space/dot entries.
Azure and WebDAV coverage here is local path-contract testing, not a live-service
integration run. R03's independent missing-source behavior was still open at
this point; its subsequent fix is documented below.

**Verification:** The following completed on CodexSandbox01 with **514 passed,
0 failed, and 3 Linux-only tests skipped**:

```powershell
dotnet test yabt.sln --no-restore --configuration Release `
  --verbosity minimal -m:1 --disable-build-servers
```

The skipped cases require native Unix backslash filenames. Windows source
selection and restore refusal tests use real extended-path fixtures. Schema
path checks covered 75 valid/invalid cases across the five manifest schemas;
`git diff --check` also passed.

**Provider-boundary refactor on 2026-10-04:** `ObjectPath` now provides exact,
ordinal logical identity with canonical `/` separators. Native selectors are
converted only at the filesystem boundary; logical inputs reject backslashes.
`IObjectStoreNamespace` supplies destination-specific path validation and
comparison, including collisions between implicit parent folders. Shared
synchronization no longer chooses path rules from the host OS.

Filesystem source selection, overlap checks, directory creation, restore
staging, and commits moved behind provider capabilities. Backup preflights a
stable snapshot of projected names before reconciliation; content stays lazy.
Restore validates its expanded desired tree. Mapping failures use the ordinary
contextual failure/recovery path, with no skip, rename, or override behavior.
Later runtime failures can still leave validated files as rerun checkpoints.

**Refactor verification:** The full Release suite on CodexSandbox01 completed
with **580 passed, 0 failed, and 3 Linux-only tests skipped**, using the command
above. Coverage includes native Windows directories with different case rules,
synthetic case/accent/Unicode-equivalent namespaces, restore live-prefix scoping,
layout changes during lock acquisition, alias refusal, and staging ownership.
`git diff --check` and CRLF checks passed. Linux native execution remains
unverified; supported filesystem semantics and explicit refusal of unsupported
namespaces are documented in [architecture.md](architecture.md).

### R02: Deduplication can mutate files outside the archive

**Code:**
[FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs)
, `GetFolderItemsAsync` , `ResolveObjectPath` , and guarded mutations.

**Reproduction:** Under a unique temporary fixture, create sibling `archive` and
`outside` directories. Make `archive/linked` a junction to `outside` . Set the
archive descriptor's `histPrefix` to `linked/history` . Put two identical 20 KB
historical files under `outside/history` in a timestamp occurrence directory.
Run `deduplicate` . It exits successfully, deletes one external duplicate, and
writes reference/catalog metadata outside the configured archive root.

**Cause and impact:** Listing checks the final directory for reparse attributes,
but that directory can be ordinary while an ancestor is linked. Path resolution
validates lexical containment only. Destructive maintenance consequently escapes
the configured root.

**Proposed direction:** Validate every existing descendant path component
consistently for listing, reading, writing, moving, deleting, locking, and
staging. Preserve the documented ability to configure a linked directory as the
store root itself.

**Acceptance checks:** Test direct and nested linked ancestors in live, history,
and `.yabt-tmp` paths. All external fixture contents must remain unchanged after
a refused operation. Exercise junctions as well as symbolic links where
available; ensure the important Windows case does not disappear behind a skipped
test.

**Follow-up scope agreed with Richard:** Junction approval belongs exclusively
in the containing folder's `.yabt-policy.json` , naming the exact child link and
expected destination. There is no CLI bypass. Unapproved, changed, or unresolved
links are errors, not empty or silently omitted branches. Source approval grants
read traversal only and does not authorize archive or restore-destination
mutation. Archive-side approval remains subject to a separate safe ownership,
locking, and same-filesystem staging design. Restoring a policy must not
activate destructive external permissions.

Cycle prevention is part of R02: compare each entered directory's physical
identity with that logical branch's ancestors, including across folder-local
listing and nested ZIP projections. Reject cycles without an override. Preserve
legitimate repeated source aliases in distinct branches, and separately reject
physical overlap with archive storage, source history, and provider staging.
File hard links do not create directory-recursion edges; their handling is
tracked in R09.

**Implementation on 2026-09-21:** The filesystem provider checks every existing
descendant component at its operation boundaries, including conditional mutation
and lock/staging paths. Source traversal has a separate, once-configured read
capability; ordinary archive stores and restore destinations do not receive
policy approval. The policy format, schema, package self-hash, and ZIP identity
preserve the exact child-name/expected-target approvals. Policies without
approvals keep their prior canonical identity. Restore produces native
directories and preserves policy bytes without activating mutation permissions.

The initial native identity support covers local Windows directories and Linux
inode/mount identities. Approved traversal involving UNC storage or ambiguous
Linux bind-mount aliases is refused; unsupported native identity support does
not affect ordinary trees without links. Concurrent external directory/link
topology changes remain outside the safety contract. See
[folder policy documentation](yabt-policy-format.md#directory-links) for the
supported scope. Linux-specific native paths still require a Linux runtime test
run.

**Verification:**
`dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal -m:1 --disable-build-servers`
completed on CodexSandbox01 with **430 passed, 0 failed, 0 skipped**. Coverage
includes 19 junction integration cases, the original external-history
deduplication escape, restore staging, direct selected links, relative targets
through an external parent, nested ZIP cycles, duplicate noncyclic aliases,
archive/history/staging exclusions, and refusal to reuse source approval for
restore mutation. Windows fixtures create real junctions without requiring
symbolic-link privileges. Policy/manifest/ZIP tests verify validation, canonical
identity, and compatibility when the new field is absent. Two existing restore
fixtures were corrected to populate their configured source live prefixes; they
previously depended on missing source folders being interpreted as empty.

### R03: A nonexistent source can become a successful empty backup

**Code:**
[ArchiveOperationContextFactory.cs](../src/Yabt.Sync/Implementation/ArchiveOperationContextFactory.cs)
, `CreateContextAsync` ;
[JsonBackupRootLocator.cs](../src/Yabt.Metadata/Implementation/JsonBackupRootLocator.cs)
, `LocateRootAsync` ;
[FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs)
, `EnsureReadyAsync`, `InitializeAsync`, and `GetFolderItemsAsync` .

**Reproduction:** Create a valid source root descriptor and a separate archive
containing `important.txt` . Invoke backup on a nonexistent child of the source
root, such as `source/typo-nonexistent` . The locator finds the parent's
descriptor. Backup exits successfully, moves the archive file into history, and
publishes an empty-folder marker and live manifest. The requested source
directory still does not exist.

**Cause and impact:** An unavailable source is treated as an empty listing.
Source readiness and target initialization are insufficiently distinguished.
Existing bytes survive in history in this reproduction, but the live archive is
incorrectly reconciled to an empty state.

**Proposed direction:** Refuse a missing or inaccessible selected source before
archive mutation. Define how source disappearance during traversal differs from
a legitimately empty directory; do not infer emptiness from failed availability
checks.

**Acceptance checks:** Missing selected roots, missing configured live prefixes,
inaccessible sources, and disappearance during traversal must fail without
publishing a successful empty state or performing unrelated removals. A
genuinely empty source must remain supported.

**Implementation on 2026-10-18:** R02's strict source listing already rejects the
original missing-child reproduction. The remaining readiness race was that the
source root could disappear after descriptor lookup and be recreated by
`EnsureReadyAsync`, allowing traversal to see an empty tree.

Read readiness now validates existing storage without writing. Filesystem
readiness attempts directory enumeration; a missing, inaccessible, or nonfolder
root fails with contextual I/O information. Azure reads container properties;
WebDAV validates the existing root collection. Writable destinations have a
separate `InitializeAsync` operation. Only mutating backup calls it; source
projections, verify, and dry runs do not. Deduplication requires an existing
readable archive in both modes. Descriptor validation remains separate.

Readiness does not provide a filesystem snapshot or make concurrent source
changes impossible. Subsequent source listing and payload failures retain their
existing error handling, preventing successful empty-state publication and
unrelated removals. A genuinely empty source remains supported.

Regression tests move the source away at deterministic boundaries after
descriptor/policy lookup and during child traversal. They cover mirror and ZIP,
backup/dry-run/verify, missing selected folders and live prefixes, genuinely
empty sources, and missing archives during deduplication. Existing archive
payloads, history, descriptors, and manifests must retain their exact contents.
Filesystem tests also exercise real Windows directory-listing denial, linked
store roots, and cancellation. Loopback HTTP fixtures verify that Azure and
WebDAV readiness issue only reads, reject missing/unavailable roots, and reserve
creation requests for explicit initialization; no live service was tested.

**Verification:** The full Release suite on CodexSandbox01 completed with
**633 passed, 0 failed, and 3 existing Linux-only tests skipped**, using the
solution test command shown above. The build reported `NU1900` warnings because
the NuGet vulnerability feed was unavailable. `git diff --check` and CRLF
checks passed. Native Linux execution remains unverified.

### R04: Backup accepts overlapping source and target roots

**Code:**
[ArchiveOperationContextFactory.cs](../src/Yabt.Sync/Implementation/ArchiveOperationContextFactory.cs)
, `CreateContextAsync` , and
[ArchiveBackupOperation.cs](../src/Yabt.Sync/Implementation/ArchiveBackupOperation.cs)
, `ApplyProjectionAsync` ; compare existing restore location validation.

**Reproduction:** Configure a source descriptor whose filesystem target is the
source directory itself. Add a ZIP policy and `original.txt` . Run backup. It
succeeds, installs ZIP artifacts in that directory, and moves the original file
and policy into its own history.

**Cause and impact:** Backup has no physical source/target overlap guard. Source
files become obsolete target objects during reconciliation, so a backup
unexpectedly mutates its input.

**Proposed direction:** Reject identical and ancestor/descendant filesystem
roots after resolving physical aliases. Share the relevant path policy with
restore and logging validation rather than implementing another inconsistent
check.

**Acceptance checks:** Equal roots, target under source, source under target,
and junction aliases must fail before mutation. Separate roots must continue to
work.

**Agreed scope and implementation on 2026-10-18:** Detection is bounded and best
effort. The provider-owned location resolver validates known overlap before
target initialization or mutation locking. Filesystem validation compares the
effective selected source subtree with the entire writable target root, covering
live data, history, root metadata, staging, and locks. Source descriptor and
initial policy read locations receive separate protection. A selected source
folder and target may be siblings beneath the descriptor root.

The check resolves known junctions and symbolic links, including linked
ancestors and missing target tails, using a bounded number of path substitutions
and the provider's directory-specific comparison rules. Exact path components
need no comparison probe. Unsupported ancestor comparison leaves overlap
undetermined; ordinary I/O failures still propagate, and destination namespace
validation stays strict. The check does not scan either tree, read payloads,
probe remote services, or write source markers. Current
production source selection is filesystem-based, so nonfilesystem targets are
undetermined. Unknown aliases and cross-provider mappings remain allowed;
preflight success is not a guarantee that storage is disjoint.

R02's existing source traversal also rejects approved links into selected
archive storage, source history, or staging. Those detected overlaps now carry
an `ObjectStoreOverlapException` inside the ordinary contextual provider error.
The synchronizer and ZIP builder recognize that evidence through nested and
aggregate failures and stop immediately, including during lazy stream reads.
They preserve earlier safe checkpoints and recovery markers, suppress unrelated
removals, and do not publish a successful replacement descriptor or manifest.
Lock release and ordinary cleanup still run. No whole-operation rollback or
snapshot guarantee is introduced. Existing link approval and cycle rules remain
unchanged; archive hard-link handling remains R09.

Regression tests cover equal and nested roots, missing targets, explicit live
prefixes, selected sibling folders, prefix lookalikes, metadata read locations,
and junction aliases through configured roots or ancestors. Backup, dry-run, and
verify share the preflight guard. Synthetic provider tests detect overlap after
a completed checkpoint, both when opening and reading source streams, through
mirror, ZIP, and nested ZIP projections. They verify that later payloads are not
read or written, unrelated target files survive, and recovery evidence remains.
Provider comparison tests also cover unsupported ancestor semantics and retain
directory-specific case comparison for differently spelled components.

**Verification:** The full Release suite on CodexSandbox01 completed with
**707 passed, 0 failed, and 3 existing Linux-only tests skipped**. This includes
74 added regression cases and the existing restore, link-safety, and ordinary
source-read recovery tests. The build reported only the existing `NU1900`
warnings for the unavailable NuGet vulnerability feed. `git diff --check` and
CRLF checks passed. Native Linux execution remains unverified; controlled
filesystem backends exercise unsupported ancestor semantics on this Windows
host.

## Further correctness findings

### R05: Filesystem shape and casing changes prevent backup completion

**Code:**
[BackupTargetReconciler.cs](../src/Yabt.Sync/Implementation/BackupTargetReconciler.cs)
, `PlanBackupRepresentationConflictsAsync` ,
`ApplyBackupRepresentationConflictsAsync` , and
[FileSystemObjectStoreNamespace.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStoreNamespace.cs)
, `AfterRemoving` .

**Reproductions:** A desired source file `item` conflicts with an existing
archive directory `item/old.txt` . Separately, on Windows, source `Report.txt`
conflicts with archive `report.txt` . Both backups fail during upload. Existing
archive bytes survive, but rerunning does not resolve either conflict.

**Agreed direction:** Classify representation conflicts and validate the desired
layout before mutation. Historize confirmed blockers before preparing their
replacement payloads. Richard accepted an incomplete live tree after interruption
because previous bytes remain in history and a rerun reconciles against the
current readable source. This does not provide automatic rollback or a frozen
source snapshot. Keep unrelated obsolete entries until desired writes succeed.

**Implementation (2026-10-19):** Backup plans blockers through provider-owned
namespace comparison and validates the desired tree with
`IObjectStoreNamespace.AfterRemoving` before executing any move. Filesystem
prospective validation retains strict name rules and accounts for comparison
semantics inherited by recreated directories. Normal provider access keeps its
existing spelling checks; generic synchronization contains no OS-specific logic.

Both file/directory transition directions and case-only file/folder changes move
the blocking representation to history. Complete folders retain `.yabt-empty`
markers and native empty descendants. Flat providers distinguish an exact object
from its same-named prefix. Dry runs and verification use virtual empty subtrees
without probing blocked paths or modifying data.

Existing manifest evidence is invalidated before mutation; failed reads suppress
unrelated removals and successful manifest publication. A later successful backup
rebuilds the manifest. Normal restore requires a valid live manifest, so recovery
from history may require manual intervention while the source is unavailable.

**Verification:** The Release solution suite passed with **769 passed, 0 failed,
and 3 existing Linux-only skips** on Windows. The 28 new cases cover both shape
transitions, case and accent equivalence, complete-folder preservation, dry runs,
explicit live prefixes, failed replacement reads and successful reruns, flat-store
object/prefix coexistence, and prospective filesystem comparison. Existing
collision tests also confirm planned blockers remain untouched when the desired
layout is invalid. Native Windows per-directory case-sensitivity checks passed;
native Linux behavior was not exercised on this host. Only the existing NuGet
vulnerability-feed availability warnings occurred.

### R06: Policy settings are accepted but ignored

**Code:** [FolderPolicy.cs](../src/Yabt.Core/Models/FolderPolicy.cs) ,
[MirrorArchiveFormatHandler.cs](../src/Yabt.Format.Mirror/Implementation/MirrorArchiveFormatHandler.cs)
, and
[ZipArchiveFormatHandler.cs](../src/Yabt.Format.Zip/Implementation/ZipArchiveFormatHandler.cs)
, `ProjectBackupAsync` .

**Reproduction:** Give the ZIP handler an in-memory source containing
`secret.env` and a policy with `excludePatterns = ["**/secret.env"]` . The
generated ZIP still contains that file.

**Inspection:** Include/exclude patterns and folder options are stored or
serialized but are not applied to projection. ZIP compression uses global
options. The policy documentation presents a draft shape containing these
fields, so whether to implement them now is a design decision; silently
accepting ineffective settings is the immediate problem.

**Implemented resolution:** Richard selected warning-and-ignore handling for
unknown optional configuration properties and recognized but unimplemented
optional settings. Mirror and ZIP now warn for nonempty `includePatterns` ,
`excludePatterns` , and `options` before projection, including nested policies.
The warnings explicitly say that excluded files may still be archived and that
ZIP uses runtime compression settings. Filtering and per-folder compression
remain unimplemented; this fix makes that behavior visible.

Root/layout and policy readers warn about unknown properties. The selected
filesystem resolver diagnoses ignored extensions and credential references.
Registered runtime options inspect consumed sections, including custom Azure
sections and nested retry options. Arbitrary host/third-party settings and
unused runtime sections are outside this inspection. Warnings identify the file
or section and property path, never setting values, and repeat at most once per
source/property pair per operation, including concurrent traversal.

Invalid JSON, invalid known types/values, unsupported required formats/providers,
directory-link approvals, and integrity metadata remain errors. The Azure
descriptor allowlist remains strict, including unused declarations, to preserve
its credential boundary. Exact source policies and root descriptors still
round-trip unchanged. Restored policies are payload; their archived settings
are not activated or warned about. Transient policy diagnostic provenance never
enters serialized manifests or changes their identity.

**Regression coverage:** Reader and resolver tests cover unknown fields,
unsupported optional settings, malformed known values, safe messages, and
strict Azure declarations. Handler and integration tests cover mirror/ZIP roots,
nested mirror/ZIP policies, repeated backups, ignored exclusions, global ZIP
compression, unchanged manifest identity, and exact policy restoration without
active-policy warnings. Common/runtime tests cover concurrent warning
deduplication, options reload, case-insensitive binding, custom sections, JSON
source identification, and hosts without logging services. Manifest tests reject
unknown diagnostic fields rather than silently accepting them.

**Verification (2026-10-19):** Full Release solution testing completed with
**854 passed, 0 failed, and 3 Linux-only tests skipped** on Windows. This adds
85 regression cases over the R05 baseline. The command was:

```powershell
dotnet test yabt.sln --no-restore --configuration Release `
  --verbosity minimal -m:1 --disable-build-servers
```

Both JSON schemas parse successfully, and `git diff --check` passes. Native
Linux behavior was not exercised on this host.

### R07: `verify-restore` can dereference an unavailable entry

**Code:**
[RestorePathVerifier.cs](../src/Yabt.Sync/Implementation/RestorePathVerifier.cs)
, `CompareDirectoryAsync` and `EnumerateEntries` .

**Static finding:** The union of names includes
`restoreEntries.UnavailableNames` . A restore-only junction or attribute-read
failure can therefore leave both entry lookups empty. The source-missing branch
increments `RestoreOnlyCount` and dereferences `restoreEntry` when Information
logging is enabled. Without that logging, it still misclassifies the unavailable
item. Existing tests using `NullLogger` miss the exception path.

**Reproduction (2026-10-21):** The Release CLI from the R06 baseline was run
against an empty source and a restore tree containing a directory junction.
With the default Information logging enabled, it reported the unavailable
reparse point and then terminated with an unhandled `NullReferenceException`
inside `CompareDirectoryAsync` . The fixture's external target remained intact.

**Implemented resolution:** Handle unavailable names from either side before
classifying source-only or restore-only entries. Existing incomplete-listing
guards remain in place because a failed enumeration cannot prove absence.
Continue comparing readable siblings and return a nonidentical result whenever
any path could not be compared. The CLI maps that result to exit code 1.

`UncomparedItemCount` now counts distinct ordinal relative paths while the
diagnostics retain every failure reason from both sides. The same path therefore
counts once when both sides fail to inspect a link or open a file. Equal leaf
names in different folders still count separately. This changes neither archive
layout nor durable metadata, and verification remains read-only.

**Regression coverage:** Ten new cases exercise unavailable source/restore
entries against missing paths, ordinary files, directories, and another link.
The fixtures use Windows junctions or Unix symbolic links and include both
Information-enabled logging and a null logger. Readable equal, different, and
one-sided siblings remain comparable after an unavailable entry. Nested paths
with the same leaf name count separately, while an exclusively locked file on
both sides counts once and retains both operating-system failure reasons.
External target contents remain unchanged. All 22 focused verifier tests pass.

**Verification (2026-10-21):** Full Release solution testing completed with
**864 passed, 0 failed, and 3 Linux-only tests skipped** on Windows:

```powershell
dotnet test yabt.sln --no-restore --configuration Release `
  --verbosity minimal -m:1 --disable-build-servers
```

The rebuilt CLI completed the junction reproduction with exit code 1, one
uncompared path, zero falsely classified one-sided items, and one compared
healthy sibling. It preserved the external target bytes. `git diff --check`
passes. Native Unix symbolic-link behavior was not exercised on this host.

### R08: Cancellation may release ownership before mutation finishes

**Code:**
[FileSystemRestoreTarget.cs](../src/Yabt.FileSystem/Implementation/FileSystemRestoreTarget.cs)
, `CommitStagedFileAsync` ;
[FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs)
, move operations;
[FileSystemMutation.cs](../src/Yabt.FileSystem/Implementation/FileSystemMutation.cs)
, mutation worker ownership;
[YabtTaskExtensions.cs](../src/Yabt.Common/Async/Extensions/YabtTaskExtensions.cs)
, `WaitOrAbandonAsync` .

**Reproduced:** `YabtTask.Run` intentionally cancels waiting while synchronous
work can continue. A deterministic regression pauses a restore commit inside
its filesystem worker, cancels the owning operation, and starts a second
archive writer. Before the fix, the owner completed while its worker remained
blocked, allowing staging cleanup and archive-lock release to overtake the
commit. Both the successful-move and failing-move cases reproduced this race.

**Implemented:** Filesystem mutations use the provider-local
`FileSystemMutation.RunAsync` helper, which returns the standard .NET worker
task without abandoning its wait. Cancellation may prevent a worker from
starting. Once started, its actual success, acquired resource, or failure
reaches the caller before staging cleanup and archive-lock release. Successful
commits complete their bookkeeping before cancellation is observed again.

This covers root initialization, upload staging and commit, file/folder moves,
conditional replacement/deletion, and restore directory/staging creation and
commit. Conditional-lock waits and hashing still observe cancellation, with a
final check before the guarded mutation. Pre-canceled conditional replacement
and archive-lock acquisition now stop before creating filesystem plumbing.
Cleanup remains awaited without cancellation.

A slow or stalled native mutation can delay cancellation until that call
returns. Read-only abandonment remains unchanged. This fix does not claim to
resolve uncertain server-side completion of canceled remote requests.

**Acceptance checks:** Gated restore commits retain staging and their archive
lock while a second writer retries. Successful commits remain successful;
actual I/O failures after cancellation are not discarded or masked by the
cancellation. Pre-canceled provider mutations leave storage untouched, and
conditional preparation remains cancellable while retaining ownership until
its worker exits.

**Verification (2026-10-22):** The full Release solution suite completed on
Windows with **879 passed, 0 failed, and 3 Linux-only tests skipped**, including
15 new filesystem cancellation cases:

```console
dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal -m:1 --disable-build-servers
```

The two gated commit cases failed against the previous implementation before
the fix. `git diff --check` passes. Native Linux filesystem behavior was not
exercised on this host.

### R09: Hard-linked archive files need explicit handling

**Code:**
[FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs)
, file reads and mutations;
[HistoryDeduplicator.cs](../src/Yabt.Sync/Implementation/HistoryDeduplicator.cs)
, candidate selection, backing selection, and savings estimates;
[FileSystemArchiveMutationLock.cs](../src/Yabt.FileSystem/Implementation/FileSystemArchiveMutationLock.cs)
, lock-file writes.

**Evidence:** Added on 2026-09-21 at Richard's request, separately from R02. A
disposable Windows fixture reproduced shared backing through the baseline
Release CLI: create a 20,000-byte external file, hard-link it into history as
`a.bin` , and create an independent identical historical `b.bin` . `deduplicate`
retains `a.bin` and replaces `b.bin` with a reference. An in-place write through
the external filename then changes the archive's only materialized backing copy.
The private copy is gone. Savings accounting for deletion of another hard-link
name and control-file writes remain static findings requiring separate
reproductions. NTFS hard links share a file identity and contents, are not
directory reparse points, and cannot cause folder-recursion cycles.

**Cause and impact:** An archive/history file may share bytes with a live or
external filename. An in-place change through either name changes both. Such a
file is not an independent historical backing copy for deduplication references.
Deleting one hard-link name can free no payload space while the current
path-based savings calculation claims a saving. In-place writes to a hard-linked
control or lock file can also affect another name outside the archive.

**Proposed direction:** Continue treating source hard links as ordinary logical
files, with independent backup materializations and no hard-link-topology
restoration. Detect file identity and link count on filesystem archives. Exclude
multiply linked history files from deduplication deletion and backing selection;
existing references need verified independent backing. Establishing a private
copy must be an explicit supported operation. Refuse multiply linked writable
control files before truncating or writing them. Junction approval must not
waive these rules.

**Acceptance checks:** Reproduce a history/live shared-file mutation, deletion
that releases no payload storage, a reference whose only backing is multiply
linked, and a multiply linked lock file. Assert correct savings, unchanged
external content, stable independent backing, and ordinary source hard-link
backup/restore. Do not label this item resolved until those cases are tested.

**Implementation on 2026-10-18:** Archive stores now expose an explicit
`IsIndependentMaterializationAsync` check. The filesystem provider reads native
file identity and link count from an opened handle on Windows or Linux. Shared
files return false; missing or unsupported evidence and I/O failures are errors.
The check is operation evidence only and adds nothing to durable metadata.
Ordinary source reads do not require it. Azure, WebDAV, and memory stores retain
their independent-key resource model; hidden server-side resource aliases are
outside the filesystem hard-link detection implemented here.

Deduplication retains shared historical materializations in the rebuilt catalog
but excludes them from backing selection, redundant-object deletion, and
claimed savings. Existing references require independent content even below the
tiny-file threshold, outside normal deduplication eligibility, or when no new
duplicates will be replaced. This also covers orphan references left beside
their original materialization after an interrupted command. Shared orphan
reference files cannot be discarded by cleanup.

Backing content and independence are revalidated before reference/catalog
publication and again before deletions. Each planned removal is checked again
before its hash-guarded deletion. Later failures retain original bytes and the
history invalidation marker. There is no automatic private-copy repair, policy
override, or guarantee against external programs changing archive topology
concurrently.

Filesystem control writes reject shared files before truncation or use. The
archive lock is checked before delete-on-close opening and again on its acquired
handle. The conditional mutation guard is also checked. Restore checks its
staging handle before changing timestamps or committing the staged file. Normal
atomic replacement of a destination file remains supported because replacing
one directory entry leaves the other hard-link names and their bytes intact.

Regression tests create real Windows hard links and cover shared live/history
and external/history bytes, both candidate ordering cases, dry-run savings,
all-shared groups, references whose backing becomes shared, shared orphan
materializations, and independent source hard-link backup/restore through
mirror and ZIP. Provider-neutral tests inject sharing changes during reference
and catalog publication and verify that removals stop with recovery evidence
intact. Filesystem tests cover linked controls and staging, native identity,
missing evidence, and atomic replacement without external-content mutation.

**Verification:** The full Release solution suite on CodexSandbox01 completed
with **741 passed, 0 failed, and 3 existing Linux-only tests skipped**, including
34 new regression cases. The build reported only the existing `NU1900` warnings
for the unavailable NuGet vulnerability feed. `git diff --check` and CRLF checks
passed. Native Linux identity/link-count execution remains unverified on this
Windows host.

## Improvement opportunities

### I01: Bound ZIP memory over the complete backup

The agreed resource work is delivered in stages: small configuration/control
limits, shared spillable temporary content, ZIP ownership, streamed manifest
writing/hashing, and scalable manifest processing coordinated with I02.
Manifests growing with source file count belong to the large-data category.

**Stage 1 implemented:** Root descriptors and folder policies have configurable
1 MiB defaults; remote lock JSON defaults to 64 KiB. Readers enforce actual byte
counts, and generated root/lock documents fit the same effective limits.
Eighteen new regression cases cover missing/inaccurate lengths, byte boundaries,
exact BOM preservation, runtime reloads, and refusal without lock mutation.


**Stage 2 implemented:** A shared temporary-content factory accounts allocated
buffer capacity, spills on per-buffer or aggregate budget pressure, and owns
sealed bytes through independent reader leases. Focused tests cover threshold
crossings, large writes, concurrent reservations, runtime reload, seek/truncate,
cancellation, failed spill cleanup, and readers surviving owner disposal.

**Stage 3 implemented:** ZIP package and adjacent-manifest materializations now
use shared spillable content. Explicit backup-projection ownership reaches
nested scopes, retains sibling consistency evidence, and releases each completed
artifact group. Missing metadata still triggers eager construction with one
source read. A single ZIP creation session appends the embedded manifest before
completion; there is no whole-package byte array or second package copy.

Ten generated-stream tests exercise 32 MiB payloads with tiny and disk-only
budgets, eager/lazy construction, independent readers, exact embedded/adjacent
manifest agreement, cancellation, source errors, and early enumeration disposal.
A synchronization regression verifies prompt release across sixteen packages,
including packages below an explicit mirror-policy parent. The parent case
exposed and guarded a premature sibling-disposal error during development.

**Stage 4 implemented:** Canonical JSON hashes now feed an incremental hash
stream. Writers flush between entries, and generated live, logical-state, and
history manifests use owned spillable content for uploads. History reference
plans retain records rather than serialized byte arrays. Raw-byte validation
still precedes conditional replacement and removal.

Regression cases compare canonical hashes with the previous byte representation,
measure a generated 64 MiB hash input without retaining it, check bounded writer
chunks, and exercise disk-only history manifest creation and invalid-catalog
rebuild cleanup.

**Stage 5 implemented:** Package, live-change, logical-state, and history
manifest readers process one JSON entry at a time. Their replayable entry
collections, canonical sorting, path lookup, ZIP entry metadata, and history
deduplication plans use disposable SQLite indexes through Microsoft.Data.Sqlite.
Nested consumers share one operation database with bounded page caches. This is
scratch storage, never durable archive evidence or a metadata cache.

The package manifest's former 16 MiB cap is removed. Adjacent and embedded
copies remain byte-identical and are compared as streams. Tests round-trip a
real package containing 20,000 generated files and a manifest above 16 MiB,
with disk-only payload buffering. Other cases cover chunked/nonseekable JSON,
large individual entries, malformed documents, canceled reads, provider-specific
key equality, and cleanup after repeated backup/verify/restore operations.

Temporary records preserve zero/null required values and exact UTF-16 strings;
regressions guard the serializer's otherwise lossy handling of unpaired
surrogates. This private encoding does not alter durable JSON formats.

Temporary storage is checked at the provider boundary against known source,
archive, and restore roots before use. Later spills and option reloads recheck
locations, and approved source links cannot expose active scratch directories.
A common temporary parent containing separate data fixtures remains supported.
These remain bounded best-effort location checks, not a physical snapshot.

Resource claims are specific: payload buffering shares the configured memory
budget; manifest entries and ZIP directories no longer require whole-document
RAM. Individual records, diagnostic failure lists, and general synchronization
plans still consume memory. There is no total process-memory guarantee or
arbitrary maximum archive/manifest size; unavailable temporary disk space is an
ordinary contextual I/O failure.

### I02: Avoid repeated ZIP central-directory parsing

The earlier implementation opened a fresh `ZipArchive` and called `GetEntry`
for each output. Restoring N entries consequently repeated central-directory
parsing with quadratic aggregate work. An isolated .NET 10 probe counted about
2.1/6.6/22.9 MB of reads for 100/200/400 one-byte entries from ZIPs of only
10.9/21.8/43.6 KB.

**Implemented with I01 stage 5:** The staged package parses its central directory
once into the operation's temporary disk index. Each opened payload uses an
independent bounded byte range, framework Deflate decompression, and CRC/length
validation. Local headers must agree with indexed evidence. Concurrent readers
do not share a cursor or need to rebuild the directory.

The writer streams standard ZIP64 records and spools the central directory,
including support for more than 65,535 entries and offsets above 4 GiB. A real
streamed 4 GiB + 12,344-byte payload round-trip uses one 256 KiB source buffer
and validates the complete decompressed content and CRC. Tests also
exercise interoperability with .NET ZIP readers/writers, malformed headers,
truncation, CRC mismatches, independent readers, cancellation, and direct
instrumentation proving zero central-directory rereads after initial staging.
The large-manifest regression covers complete backup/restore handler behavior;
it is not a remote-service performance benchmark.

The `yabt-zip-change-v2` fingerprint domain prevents new generated bytes from
reusing the preceding writer's immutable package names. A first backup after
upgrading can therefore historize old ZIP artifacts and create one new
generation even for unchanged source content. Durable JSON schemas and format
version remain unchanged, and existing valid packages remain readable.

**Final I01/I02 verification (2026-10-22):** The full Release solution suite
passed on CodexSandbox01 with **1,038 passed, 0 failed, and 3 existing Linux-only
tests skipped**. The final run reported no build warnings.

```powershell
dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal `
  -m:1 --disable-build-servers
```

Independent review covered projection/manifest ownership and the ZIP framing,
range, length, and CRC logic. `git diff --check` and CRLF checks pass. Native Linux
execution and real remote-service performance remain outside this Windows run.

### I03: Prune history before current-live restore traversal

[RestorePlanner.cs](../src/Yabt.Sync/Implementation/RestorePlanner.cs)
, `LoadRestoreObjectsAsync` , previously listed the live prefix recursively and
filtered internal paths afterward. With the default empty live prefix, this
enumerated history and staging descendants unnecessarily. Restore work grew
with accumulated history, and failures listing excluded subtrees could prevent
current-live restore.

**Implemented:** Restore now uses iterative folder-local traversal through the
existing object-store contract. It prunes the configured history and staging
prefixes before scheduling descendant listings. Nested history ancestors and
ordinary live siblings remain traversable, and explicit live prefixes retain
their own ordinary history- or staging-named descendants. Root metadata names
remain exact-object exclusions, so a same-named virtual folder cannot hide
unmanifested live content.

Manifest validation, duplicate-object rejection, hidden empty-folder-marker
lookup, and destination safety traversal remain in place. Provider validation
while listing a parent still applies; pruning does not bypass unsafe entries or
an unreadable parent. No provider or durable-format change is required.

**Regression evidence:** Ten tests cover three layouts, forbidden history and
staging descent, live listing failures, and unmanifested content. Growing each
excluded subtree from one object to 129 leaves visited-folder and yielded-object
counts unchanged. The tests inspect actual provider calls rather than elapsed
time. Seven cases failed against the preceding implementation; all ten pass
after the fix. This is traversal evidence, not a remote-service benchmark.

**Verification (2026-10-22):** The full Release solution suite passed on Windows
with **889 passed, 0 failed, and 3 Linux-only tests skipped**:

```powershell
dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal `
  -m:1 --disable-build-servers
```

`git diff --check` passes. Native Linux execution and remote-service performance
were not exercised on this host.

### I04: Separate orchestration responsibilities and strengthen provider tests

The preceding `ArchiveSynchronizer` was roughly 6,000 lines and owned backup
comparison, restore planning/execution, projection lifetimes, recovery markers,
manifest publication, and historization coordination. The concern was the
reviewability of interactions among those invariants. This finding did not
establish an additional data-loss defect.

**Implemented:** The public synchronizer is now a 143-line command facade. The
extracted components contain the existing behavior and have explicit roles:

- `ArchiveBackupOperation` and `ArchiveRestoreOperation` own their command
  lifetimes, location checks, locks, and completion/failure gates.
- `RestorePlanner` expands format projections and validates the desired
  namespace. Its returned `RestorePlan` owns projection lifetimes.
- `RestoreReconciler` compares the destination with that plan. `RestoreExecutor`
  owns bounded preparation, per-file commits, and deferred extra removal.
- `BackupTargetReconciler` owns target traversal, representation conflicts, and
  historization selection. `ArchiveContentTransfer` compares and prepares bytes.
- `ArchiveManifestStore` handles manifest I/O. The operation-owned
  `BackupManifestTransaction` and `RestoreManifestTransaction` retain recovery
  state and order invalidation, publication, and marker cleanup.
- Root descriptor handling, context resolution, logical path rules, and logging
  have focused components. Native filesystem semantics remain in providers.

The transaction objects borrow manifest models; callers still dispose them after
all consumers finish. Failure never triggers automatic marker cleanup. Backup
still publishes live evidence before completing history recovery. Restore still
completes history recovery before publishing logical-state evidence. Started
restore workers still finish before staging, projections, and locks are
released. Source failures still suppress unrelated removals and successful
publication.

**Provider evidence:** Six reusable cases run against disposable filesystem
stores, the actual Azure SDK against controlled HTTP responses, and WebDAV's
actual HTTP provider against a controlled loopback server. They cover immediate
traversal and exact keys, create-if-absent uploads, conditional mutations,
whole-folder moves including markers, interrupted uploads, and lock contention
with reacquisition. Provider-specific cases add listing pagination/failure,
post-read ETag races, file/prefix conflicts, copy/move failures, and
preservation of a replacement lock owner's document during release.

These fixtures exercise protocol behavior, not deployed-server compatibility.
They do not establish periodic renewal or cancellation of an active operation
after renewal failure. The initial provider-contract run used neither Azurite,
a live Azure account, nor a deployed WebDAV server. Linux/ext4 acceptance under
Docker Desktop is recorded below; deployed-service acceptance remains pending.

Fifteen focused manifest cases inject failures at publication boundaries and
verify that old evidence or invalidation markers survive, invalid evidence is
not trusted, and a later successful operation recovers. Existing end-to-end
tests retain coverage of partial restores, canceled workers, projection
ownership, source failures, namespace conflicts, and history cleanup.

The class extraction and transaction wiring are separate commits. No path
comparison rules, durable formats, payload algorithms, or restore blocker lookup
algorithms changed during this work.

**Commits:** `a7555f3` extracts the orchestration components; `485601d` adds
provider contracts; `75f0433` isolates manifest recovery transactions and adds
failure-boundary tests.

**Verification (2026-10-22):** The full Release solution suite passed on
CodexSandbox01 with **1,094 passed, 0 failed, and 3 existing Linux-only skips**.
The build reported no warnings. A final change to reuse the shared WebDAV lock
contention case also passed its focused test after the solution run.

```powershell
dotnet test yabt.sln --no-restore --configuration Release --verbosity minimal `
  -m:1 --disable-build-servers
```

Independent review compared extracted method bodies and transaction ordering
with the preceding implementation. `git diff --check` and CRLF checks pass.

**Scaling follow-up implemented:** Restore previously scanned all H planned
history moves before each of F file commits, including completed moves. This
entailed F x H checks; 10,000 of each meant roughly 100 million checks. Directory
classification also scanned the complete desired-directory list, including for
unchanged-item debug messages when debug logging was disabled. Backup cleanup
scanned all selected obsolete folders both during selection and for each object.
These were code-derived work estimates, not elapsed-time measurements.

`RestoreHistoryPlan` now owns ordered move records, a provider-comparer path
index, and pending state in `TemporaryIndex` storage. Each commit probes its exact
path and ancestors, then applies only matching blockers in original plan order.
Pending moves are removed only after successful historization. Unrelated extras
remain deferred until desired writes succeed; a failed blocker leaves its move
pending and prevents that file's commit. `RestoreReconciliation` owns this index
through execution and releases it on failure or completion.

`RestoreDirectoryIndex` replaces retained directory arrays with indexed
membership and depth/ordinal traversal. Planner path sets also use temporary
storage, and reconciliation reuses the plan's membership queries. Disabled debug
logging skips unchanged-item classification. `BackupCleanupFolderIndex` orders
candidates in temporary storage and probes strict ancestors, preserving exact
object versus same-named prefix semantics and original historization order.

These use the existing SQLite scratch infrastructure and bounded page cache, not
a new persistent cache. Lookup probe counts depend on path depth rather than the
number of unrelated moves/folders. Disk indexes still have search, serialization,
and sorting costs; this is not a claim of constant total I/O per operation.

Regression tests count comparison/membership work across increasing workloads,
including the actual restore executor with unrelated moves pending throughout
file writes. Additional cases cover ordering, provider comparison, original
spellings, exact object/prefix distinctions, disabled debug logging, cancellation,
failed historization, and scratch lifetime. The executor fixture performs 224
provider comparer calls for 32 writes plus 32 unrelated obsolete files, and 896
calls for 128 of each. Temporarily reinstating the prior scan made the regression
fail with 1,216 and 17,152 calls respectively. These counts combine hashing,
equality, and comparison calls; they do not measure elapsed time or native I/O.
The indexed implementation was restored before final validation.

**Follow-up commits:** `6584682` implements restore indexing; `6309815` implements
backup cleanup indexing. `AGENTS.md` now requires active algorithm-cost review,
appropriate scratch indexes, and deterministic scaling regression tests.

**Follow-up verification (2026-10-22):** All 35 new focused cases pass. The full
Release solution suite passed with **1,129 passed, 0 failed, and 3 existing
Linux-only skips**, with no build warnings. Independent review checked blocker
semantics, provider comparer ownership, deferred cleanup, and disposal. The final
`git diff --check`, UTF-8, CRLF, and Markdown wrap checks also pass.

**Memory follow-up (2026-10-23):** The new real-filesystem profiling runner
separates normal public-command timings from retained-memory diagnostics, with
bounded watchdogs and disposable fixtures. See
[plan-memory-measurements.md](plan-memory-measurements.md) and the committed
[numerical results](plan-memory-results.json). Mirror fixtures cover 1,000,
10,000, and 30,000 payload files; a 10,000-file ZIP case checks format-specific
ownership. Heap snapshots caught an inline async-completion-stack measurement
artifact; all retained samples were repeated after adding quiescent checkpoints.

The corrected evidence identifies the restore plan's per-file objects, openers,
projection owners, and dependency graph as the main persistent-memory target.
Enumeration retains little additional managed memory but still allocates
substantially. Reconciliation dictionaries and reporting/backup state also
remain candidates for transient-memory and allocation work. Normal backup and
restore timings do not exhibit quadratic growth in these measured cases;
phase-specific backup ownership, provider-call counts, and actual scratch I/O
volume were not measured here.

**Incremental backup, first slice (2026-10-26):** Mirror backup now discovers and
validates one complete immediate folder namespace, applies its files, and then
visits pending child folders. This includes explicit mirror-policy scopes.
`IArchiveBackupFolderProjector` and the owned `ArchiveBackupWorkUnit` make the
boundary explicit; handlers without that contract retain complete-projection
preflight. A selected root ZIP remains one complete projection.

Sibling files and child-directory names are validated together before the unit
moves blockers or writes content. A later unit can fail after earlier safe
commits. Unrelated cleanup and successful manifest publication remain deferred
until the whole traversal succeeds. Prospective ancestor rules remain active
for dry runs and verification; mutating traversal uses the actual resulting
namespace. Package artifact counts and release routing preserve complete group
ownership, and policy-scope lookup now probes ancestors rather than scanning all
earlier scopes.

The pending-folder frontier and desired-folder membership use disposable
temporary indexes. This removes the full projected-object snapshot for ordinary
mirror traversal, but target caches, policy projection state, and individual wide
folders can still retain substantial memory. Existing root-manifest work also
remains. This slice establishes earlier useful writes; it does not yet persist
checkpoints or claim measured timing or total-memory improvements.

Validation: the Release solution suite passed 1,144 tests with three existing
Linux-only skips. After the final projection guards and regressions, the Sync
suite passed 416 tests with the same three skips, bringing distinct passing
coverage to 1,149 tests. Final builds had no warnings. Regression coverage proves
commits before descendant enumeration, incomplete-listing isolation, late-error
and cancellation recovery, read-only traversal, nested case transitions, package
ownership across folder units, and rejection of hidden projection collisions.
Independent review and UTF-8, CRLF, Markdown-wrap, and diff checks passed.

**Durable backup progress (2026-10-26):** Mutating backup now retains a private
machine-local SQLite journal of unit completion and immediate child frontiers.
Updates are atomic and flushed in bounded batches, with controlled-failure
flushing. Restart uses fresh rooted traversal and current policies, prioritizes
unfinished subtrees, and revalidates every completed folder before success.
The journal contains no payloads or authoritative comparison evidence. A crash
can require repeating the latest batch; journal failures fall back to fresh
traversal. Successful final publication removes the database; the empty lease
file remains to avoid lock-name races.

Provider-owned opaque identities bind progress to configured locations without
retaining connection secrets. Location checks reject overlap with operation data.
Native regular-file identity checks reject shared or linked journal controls,
reusing the filesystem provider's existing evidence implementation. Dry runs and
verification do not open progress storage. Package work completes only after
every artifact consistency check succeeds; partial ZIP construction is never
persisted. Generic artifact groups spanning folders conservatively leave their
earlier units pending.

**Incremental mirror restore (2026-10-26):** Current-schema plain mirror restore
now applies complete immediate folder units after root-manifest validation and
destination safety checks. Its manifest folder index and frontier are disk-backed;
projected files, reconciliation state, and dependencies are retained per unit.
The existing bounded worker pipeline and one operation-wide mutation session
preserve file validation, history allocation, invalidation, and deferred cleanup.
Manifest-bound local progress prioritizes unfinished branches before revalidating
completed folders. Packaged and legacy archives retain complete-plan fallback.

Regression tests cover writes before descendant enumeration, late listing and
payload failures, cancellation, dry runs, empty-folder reporting, and restart
priority. Combined validation covers 1,192 passing tests and three existing
Linux-only skips after correcting a test-only shared-file read. NuGet advisory
lookup reported an unavailable feed; compilation had no code warnings. Package
integration and timing validation follow separately. Native Linux and
deployed-service acceptance from I04 remain pending.

**Restore finalization memory (2026-10-26):** Final logical-state publication now
collects current destination stat evidence one folder at a time. Desired hashes,
duplicate detection, and result entries use temporary disk indexes. This removes
the remaining whole-tree destination dictionary from successful finalization
without reopening payloads or weakening missing-file/listing-failure checks.
Four focused cases validate one listing per folder, independent result ownership,
fresh stat evidence, and failure gates. Other per-folder and per-package state
can still grow with their unit size; this is not a total process-memory bound.

**Package checkpoints (2026-10-26):** Backup records complete adjacent package
groups separately and prioritizes unfinished groups inside their already
validated containing folder. Completed groups are revalidated afterward through
a stable linear partition. Naming discovery still precedes the folder's writes;
generic scattered groups remain conservative.

Current-schema mirror restore routes each adjacent package group as a complete
owning subtree unit, including nested formats. Its full namespace and ordinal
ownership are validated before mutation. Package evidence is also validated
before directory-only changes when existing files match. Root ZIP, legacy, and
scattered custom groups retain complete-projection planning. Partial compressor
state and within-folder listing cursors are not persisted.

Cleanup preserves the structural ancestors of nested destination history and
historizes only ordinary obsolete siblings. New regression coverage includes
partial ZIP/manifest pairs, changed completed packages, sibling priority,
nested-package restart, scope escapes, directory-only integrity, and history
ancestor protection. Timing and normal-execution overhead measurements are
recorded in the dedicated results report below.

The complete Release suite passes **1,212 tests**, with three existing Linux-only
skips. The final linear priority partition and package safety checks also pass a
27-case targeted rerun. No compiler/analyzer warnings were reported; NuGet's
advisory lookup remains unavailable in this environment.

**Incremental resume measurements (2026-10-26):** All 28 filesystem scenarios
passed, covering 44 fresh worker processes. See
[incremental-resume-measurements.md](incremental-resume-measurements.md) and the
[numerical results](incremental-resume-results.json). With 3,000 mirror files,
progress reduced restart-to-next-new-write from 11.25 s to 0.89 s for backup and
from 11.44 s to 3.07 s for restore. Total completion time stayed similar because
completed units still undergo current validation. Whole-root ZIP restore showed
no useful acceleration, consistent with its complete-package boundary.

Three uninterrupted 1,000-file mirror samples per setting show about 1.9% higher
median backup time with progress enabled. Restore medians remain within observed
variation, including a slower third enabled sample. These are local tiny-file
measurements, not universal overhead bounds. Restart comparisons have one sample
per setting and use cooperative cancellation, which flushes safe progress;
abrupt process failure can lose the latest batch. The separate crash regression
checks rollback of unflushed progress. Sampled process peaks do not replace the
earlier retained-heap investigation or establish a total-memory bound.

The final diagnostic runner also validates fixture ownership before worker
execution. Four small smoke scenarios and seven invalid-setup rejections pass;
these checks are separate from the timed matrix. Regenerating the numerical
summary reproduces the saved report exactly.

The agreed folder/package slices are implemented. Root-wide manifest work,
within-unit memory, native Linux acceptance, and deployed-service acceptance
remain explicit follow-up areas. No partial ZIP state or within-folder cursor is
persisted. Implementation commits are `05cf375`, `7bf362b`, `90bf46c`, and
`b96aaf3`, following the initial folder-backup slice `e84d84e`.

**Abrupt-process recovery acceptance (2026-10-26):** Seven real-filesystem
scenarios now terminate the complete worker process tree at deterministic
mutation boundaries and resume in a fresh process. See
[the acceptance tests](../tests/Yabt.Sync.Tests/ProcessCrashRecoveryTests.cs).
The test gate blocks inside existing structured log callbacks; production code
has no new fault-injection hooks. The parent confirms that managed cleanup did
not run and waits for both the test launcher and actual worker to exit before
inspecting the files or starting recovery.

Mirror backup and restore stop after historizing an old file but before
installing its replacement, after committing a replacement, and after writing
the final manifest while its invalidation marker remains. Backup additionally
stops after uploading a new ZIP while its adjacent manifest is still absent.
The restore commit case expands an outer ZIP containing another ZIP. One backup
case changes the source again during downtime and preserves both older content
versions while recovering to the current source bytes.

Every recovered operation must reopen its surviving progress journal without
fallback, acquire its locks, and complete. Checks cover exact payload and
descriptor bytes, preserved history, deferred obsolete-item cleanup, valid
manifests, byte-identical embedded/adjacent package manifests, empty folders,
marker cleanup, and journal removal. The journal flush threshold is deliberately
larger than each fixture, so termination bypasses the normal disposal flush.
The parent does not open SQLite before the fresh recovery worker.

These are correctness checks with bounded process watchdogs, not timing
benchmarks. They establish abrupt-process recovery on the tested Windows
filesystem; they do not establish durability through power loss, remote lease
failure, or native Linux behavior. No production recovery changes were needed.
Native Linux and deployed Azure/WebDAV acceptance remain the next validation
steps under I04.

Validation: all seven focused cases pass. The complete synchronization test
project passes **478 tests**, with three existing Linux-only skips. Independent
review checked crash boundaries and process ownership; UTF-8, CRLF, and diff
checks pass. Compilation reported no code warnings; NuGet's advisory-feed lookup
remains unavailable. Other projects were unchanged and were not rerun for this
test-only slice.

**Linux/ext4 acceptance (2026-10-26):** The dedicated `yabt-linux` Docker Compose
environment runs the real Release solution suite on Docker Desktop's Linux
backend in CodexSandbox01. Its persistent `yabt-linux-workspace` volume is
mounted once at `/work` and was verified as ext4. The checkout, test fixtures,
temporary content, progress journals, SDK state, and NuGet cache use that mount.
The container runs as UID 10001 with two CPUs and a 3 GiB memory limit. DSA's
containers and configuration are separate. See the
[runner instructions](../tools/linux-acceptance/README.md).

The pinned SDK image provides Ubuntu 24.04.5, .NET SDK 10.0.403, and runtime
10.0.14 on the WSL 2 Linux kernel `6.18.33.2-microsoft-standard-WSL2`. Preflight
rejects root execution and unsupported fixture storage; explicit negative
checks confirmed rejection of root and OverlayFS temporary storage. Actual
provider tests exercise native file identity, directory links, hard links,
namespace rules, and mutation behavior.

The first run exposed three test portability failures. Two permission assertions
examined only the deepest exception and missed the surrounding
`UnauthorizedAccessException`. A cyclic symlink fixture used directory removal
where Unix needs to unlink the symlink entry. Fixing those test helpers required
no production changes. All 29 focused Linux cases then passed; the corresponding
Windows run passed 26 cases and skipped the three Linux permission tests. The
Windows rerun used ordinary host execution after the restricted sandbox's test
launcher could not connect to its worker.

**Final full-suite result:** **1,199 passed, 0 failed, 26 expected platform
skips**, across all ten test projects. Every skip concerns Windows or
case-insensitive filesystem behavior and is individually recorded. All 13
mandatory acceptance rows passed: three Unix literal-backslash cases, three
native permission-denial cases, and all seven abrupt-process recovery cases.
The build reported no warnings. The permission tests verify failures during
readiness, later folder enumeration, and payload reads without modifying source
content or silently reporting an empty tree.

The accepted source revision is `12c5f10c06e4ed1500283f196b14fad772bcc87b`.
Environment setup is commit `92ddfa5`; fixture corrections and durable runner
guidance are commit `12c5f10`. Logs, ten TRX files, the container metadata, and
`acceptance-summary.json` are retained under `artifacts/linux-acceptance/`, in
run `12c5f10c06e4-20261026T124729Z-290e5a5a`. The Linux logs and TRX files also
remain in the persistent volume.
The host summary confirms complete project coverage, required passing rows,
successful runner/result export, and no validation issues.

This completes the current Linux/ext4 correctness acceptance, including fresh
process recovery under WSL 2. It does not establish hardware power-loss
durability, other filesystem or mount configurations, deployed Azure/WebDAV
compatibility, or performance equivalence with Windows. Deployed-service
acceptance remains the next I04 validation step.

**Deployed-service acceptance (2026-10-26):** The opt-in
[service runner](../tools/service-acceptance/README.md) now exercises the real
providers and synchronization API. Ordinary tests do not contact deployed
services. Each live case owns a fresh random subtree beneath the configured
test location; passing cases remove only that subtree and verify cleanup.
Failed cases retain any created fixture for diagnosis. Credentials and raw
diagnostics remain outside Git, while public summaries contain only revision,
platform, selected provider, case names, and outcomes.

Azure Blob Storage passed **all nine cases on Windows and all nine on
Linux/ext4** at source revision
`db51fbb0ac10` (full revision is recorded in each summary). These include:

- Exact immediate names and traversal, create-if-absent, conditional
  replacement/deletion, whole-folder moves, interrupted source-stream upload,
  archive-lock contention/reacquisition, and exact-object/same-prefix behavior.
- Mirror and ZIP backup/restore, unchanged reruns, replaced/deleted/added files,
  preserved archive and destination history, byte-for-byte archive verification,
  exact restored content and timestamps, and empty folders.

The accepted public summaries are under `artifacts/service-acceptance/`:

- `db51fbb0ac10-windows-azureblob-20261026T184929Z-cede1f99`
- `db51fbb0ac10-linux-azureblob-20261026T184930Z-4fe72fb5`

Both summaries report nine expected passing rows and no validation issues.
Private TRX files and process output remain under the private settings
directory's `runs` folder. The Linux run uses a temporary unprivileged companion
container, the existing Linux workspace volume, a read-only credentials mount,
and private tmpfs diagnostics exported before container removal. The first
runner attempts exposed two harness defects: PowerShell stream-copy return
values contaminated exit-code checks, and Docker's archive endpoint missed the
tmpfs report contents. Both were fixed and the accepted runs repeated with
verified report export. No production provider change was needed for Azure.

pCloud WebDAV initially returned **HTTP 401 during isolation preflight**, before
fixture creation. Richard subsequently confirmed the configured credentials in
an incognito browser. The following VM probes, without an authentication-code
change, succeeded with GET 200 and PROPFIND 207 at the account root; missing test
paths returned 404. The cause of the earlier authentication rejection was not
established. Authentication is no longer the acceptance blocker.

**Authenticated pCloud results:** After correcting cleanup verification,
**three of nine cases passed on each platform; six failed on each** at revision
`174c04c2e763`. Traversal/exact names, create-if-absent, and failed-upload recovery
passed, including confirmed cleanup. Conditional mutations, lock contention,
the native-empty-folder move case, and mirror/ZIP synchronization stopped because
newly uploaded objects did not yet provide the strong ETag required for safe
conditional mutation. The whole-folder move case returned an HTTP 403 when
immediately reopening the moved destination. These remain acceptance failures;
the production guards and operation assertions were not relaxed.

The public summaries are under `artifacts/service-acceptance/`:

- `174c04c2e763-windows-webdav-20261026T192248Z-215d490b`
- `174c04c2e763-linux-webdav-20261026T192249Z-15e03e37`

Additional disposable protocol probes narrowed the behavior:

- PUT returned 201 and GET immediately returned the complete expected bytes,
  but GET omitted ETag through 941 ms in one probe. A strong ETag appeared at
  1,222 ms. These observations diagnose availability, not a performance bound.
- Once that validator was available, deliberately incorrect `If-Match` values
  on GET, PUT, and DELETE all returned 412 and preserved the original bytes.
  A later HEAD and `DAV:getetag` also supplied a strong validator. The existing
  logical-hash mismatch tests alone would not establish server enforcement of
  HTTP preconditions; these direct probes did.
- A separate folder MOVE returned 201, followed by destination GET responses
  of 403 and then 404 throughout a 5.4-second observation window. An earlier
  failed move fixture was readable on a later inspection. Do not assume a
  successful MOVE response establishes immediate readable destination state.
- Moving to an occupied collection with `Overwrite: F` returned 412.
- A probe collection DELETE returned 207 Multi-Status; subsequent PROPFIND
  responses contained successful collection properties, not merely missing
  property statuses. Complete removal was not established. Failed test/probe
  subtrees remain for diagnosis; unrelated storage was not cleaned up.

The test harness now issues DELETE only once, rejects ambiguous DELETE 207,
and waits up to 30 seconds for an exact owned collection to return 404 after a
successful deletion. Only verification 207 is retried, with capped backoff;
authentication, transport, other statuses, cancellation, and timeout still
fail. Ten controlled handler checks covered these boundaries, including a
persistent-207 timeout at 30.03 seconds. All 54 ordinary WebDAV regression tests
also passed. This cleanup-only wait does not change production semantics.

**Backlogged scope (2026-10-27):** Add bounded provider-owned handling of delayed
strong validators and visibility after acknowledged writes/moves/deletes. Do
not put pCloud-specific delays into the synchronizer or make tests sleep before using
the provider. Bind bytes to the same strong validator used by the conditional
mutation; never combine an earlier unvalidated read with a later ETag, replace
`If-Match` with unconditional mutation, or interpret a temporary failure as an
empty/missing source. Examine per-resource 207 results rather than accepting
every 2xx as complete success. Timeouts and incomplete operations must remain
contextual I/O failures. Verify the behavior with controlled delayed-response
fixtures and repeat all nine WebDAV cases on both platforms before accepting
pCloud compatibility. Provider-specific reruns do not require repeating Azure.

Local Release regression verification completed with **1,219 passed, zero
failed, and six expected Linux-only skips**. The new mirror/ZIP scenarios also
passed a local memory-store smoke check, and explicit opt-out skipped the two
selected Azure synchronization rows without remote calls. NuGet vulnerability
metadata retrieval produced an availability warning during the Windows build;
it did not prevent compilation or tests.

This closes deployed Azure correctness acceptance for the documented cases. It
does not establish pCloud compatibility, production CLI WebDAV store selection,
long-duration remote lock renewal, abrupt-process cloud recovery, large-data
performance, or storage-provider durability guarantees. The WebDAV CLI resolver
remains separate unfinished integration work; these tests intentionally inject
the configured provider into the synchronization API.

### Delivery priority: Synology NAS to Azure (2026-10-27)

Richard has moved pCloud and WebDAV as a whole to the backlog. pCloud is the
only currently intended WebDAV use case, principally as a source. Keep the
observed failures open; deferral does not establish provider compatibility.
Also defer WebDAV source selection, descriptor/policy resolution, and restore
destination integration. Existing service round trips use a filesystem source,
a remote archive, and a filesystem restore destination; they do not cover a
WebDAV source or restore destination.

The primary use case is Synology NAS filesystem data backed up to Azure Blob
Storage by a Linux container on the NAS, followed by scheduled incremental runs.
Richard confirmed that the intended backup covers the full approximately 34 TB
dataset. The earlier 34 GB figure was a unit mistake. Inventory the shares and
payloads to establish actual logical bytes; volume usage alone is not an exact
file-content total. Do not silently omit data from this scope.
The filesystem-to-Azure CLI resolver, streaming and
spillable content, metadata-based incremental comparison, remote archive lock,
SDK retries, folder progress, and current-live filesystem restore already
exist. Linux/ext4 and deployed Azure acceptance support this direction, but do
not establish production-container or Synology acceptance.

Read-only DSM inspection on 2026-10-27 established this deployment baseline:

- Synology DS1817+, Intel Atom C2538, four cores at 2.4 GHz, 16 GB RAM.
- DSM 7.1.1-42962 Update 9; Docker package 20.10.3-1308 is running.
- Volume 1 uses Btrfs on RAID 5, with 34 TB used of 48.9 TB shown by DSM.
- Snapshot Replication is offered for installation, not currently installed.

No NAS configuration was changed and no workload was deployed. The UI inspection
did not establish the kernel version, file count, largest file, selected shares,
or sustained upload bandwidth. Do not assume that the development VM's modern
kernel and Docker behavior apply to this NAS. Containers share the host kernel.

The first compatibility check must exercise the actual CLI/runtime image,
persistent progress, and a tiny filesystem restore on real Btrfs bind mounts.
`NativeFileIdentityReader.ReadLinux` requires `statx` file identity and link-count
evidence without a fallback. Missing evidence disables the optional progress
journal with a warning, but blocks filesystem restore controls and staging
commits. Ordinary source traversal without descendant directory links does not
need directory identities; approved links additionally require `STATX_MNT_ID`
support. If needed, implement compatible identity checks at the provider/native
boundary while preserving the existing hard-link protections.

Richard subsequently reported an ideal upstream rate of 40 Mbit/s. At that
rate, 34 trillion bytes require about 78.7 days of uninterrupted transfer;
34 TiB require about 86.5 days. These are ideal lower bounds, before protocol
overhead, small-file operations, competing traffic, retries, or source changes.
Plan for a months-long initial run. Measure useful throughput on representative
files before estimating its actual duration.

The larger confirmed scope makes sustained lock renewal, persistent progress,
network interruption, process termination, and restart acceptance essential to
the pilot. Determine file count, largest file/package, and daily changes before
deciding whether partial-object upload continuation or traversal optimization
is needed. Total bytes alone do not justify an algorithm rewrite. Size scratch
for simultaneously staged objects and indexes, not a second copy of all 34 TB.
Estimate Azure capacity, history growth, request costs, verification reads, and
restore transfer/retrieval costs using the intended region, redundancy, and
tier before the full run. Include source consistency and snapshot lifetime in
the plan for a potentially long initial backup.

#### Pilot destination and cost constraints

Richard authorized container `archive` in account `yabtdevhafner` for the NAS
pilot. Use an isolated prefix; this is distinct from the existing deployed
service acceptance container `archivetest`. The container name does not select
the Azure Archive access tier. No account settings or local credentials were
changed as part of this assessment, and no NAS upload was started.

A separate production storage account is sensible for separate credentials,
lifecycle settings, and cost attribution. Standard Blob Storage charges are
usage-based; another ordinary account does not add a fixed account rental fee.
Enabled services and features can add charges. A second account does not create
another subscription credit allowance. Production provisioning remains pending.

Richard's available monthly allowance is EUR 150, with little desired overrun.
The allowance's origin and eligibility remain to be confirmed. If it is the
Visual Studio monthly Azure credit, Microsoft limits that benefit to individual
development and testing; do not assume it funds ongoing production backup.
See Microsoft's [Visual Studio Azure credit FAQ][nas-credit-faq].

Indicative capacity-only pricing queried on 2026-10-27 from Microsoft's public
[Retail Prices API][nas-retail-api]: Standard General Block Blob v2, West Europe,
LRS, consumption pricing, first capacity band. West Europe/LRS is an estimate
assumption, not a verified production-account configuration. EUR API prices are
reference estimates; confirm the subscription's actual offer and current prices
before deployment. Azure bills storage in binary GB (GiB), so the range below
covers 34 trillion bytes through 34 TiB; inventory must establish actual bytes.

The query used `currencyCode=EUR` with the following filter, then selected the
LRS capacity and retrieval meters with `tierMinimumUnits=0`:

```text
serviceName eq 'Storage' and armRegionName eq 'westeurope'
and priceType eq 'Consumption' and productName eq 'General Block Blob v2'
```

| Access tier | EUR/GiB/month | Approximate full-capacity EUR/month |
| --- | ---: | ---: |
| Hot | 0.0172 | 545-599 |
| Cool | 0.0088 | 279-306 |
| Cold | 0.0040 | 127-139 |
| Archive | 0.0016 | 51-56 |

These figures exclude history, metadata, requests, retrieval, outbound transfer,
optional services, and applicable taxes. Initial storage charges grow as data
arrives. Cold leaves little headroom at full capacity; history grows as live
files change or disappear. There is no basis yet to promise a EUR 150 total.
Redundancy beyond LRS needs a separate estimate. See
[Blob Storage pricing][nas-blob-pricing] for billing units and cost components.

Cold data remains online but has a 90-day minimum billable retention period;
early deletion, overwrite, or tier changes can incur additional charges.
Archive is offline, has a 180-day minimum, and requires rehydration before
payload reads. YABT has no tier-selection or rehydration workflow currently.
Do not apply Archive automatically to live objects or history backing objects
that YABT may need to read. See [Azure access tiers][nas-access-tiers].

At the queried Cold retrieval price of EUR 0.0264/GiB, one full read costs about
EUR 836-919 in retrieval charges alone, before outbound bandwidth and requests.
This applies to a complete restore or full byte-for-byte cloud verification,
and illustrates the cost of unnecessary recovery reads. Server-side copying
avoids NAS transfer but can still incur Azure read/retrieval/write charges.

#### Cloud read and history-move gaps found during cost assessment

The source audit found two production prerequisites at this bandwidth and size:

1. Interrupted backup recovery can download the already-uploaded portion again.
   `OperationProgressJournal` stores unit paths and completion state, without
   per-object fingerprints, hashes, or target versions. An initial backup has
   no final change manifest until successful completion. A modifying run deletes
   its previous manifest after invalidation, and `ArchiveManifestStore` refuses
   invalidated evidence. With no trusted previous entry, same-length target
   objects are compared by reading their bytes. Completed objects survive and
   need not be uploaded again when equal, but folder scheduling alone does not
   make restart transfer-efficient. The reread can approach the entire completed
   portion of a long initial run.
2. Azure history moves of blobs larger than 256 MiB fall back to attempting a
   full download and re-upload. `AzureBlobObjectStore.TryCopyAndDeleteAsync` calls
   `SyncCopyFromUriAsync`, whose installed SDK and
   [Copy Blob From URL documentation][nas-copy-blob] impose that limit. Any copy
   exception selects `MoveByDownloadUploadDeleteAsync` unless caller cancellation
   has been requested.
   This transfers the old bytes through the NAS in addition to uploading changed
   replacement content. The synchronous API's size limit is a predictable cause,
   not only a hypothetical network failure.

Proposed remedies, not yet implemented or approved as durable-format designs:

- Publish bounded archive-side evidence for each completed folder/package,
  binding source fingerprints, actual hashes/lengths, projection identity, and
  opaque provider-owned target version evidence. Resume rediscovers source and
  policies and validates current source and target evidence before skipping
  payload comparison. Missing or stale evidence falls back to reads. Keep the
  local SQLite journal disposable, explicit byte verification authoritative,
  and unrelated cleanup/final manifest publication behind full success. A crash
  before a folder checkpoint may reread that folder, as previously accepted.
- Use the Azure SDK's server-side copy operation for large history moves, with
  source ETag and create-only destination conditions. Confirm completion before
  guarded source deletion; make cancellation, ambiguous completion, and retry
  safe. Prefer the existing SDK operation abstraction over a custom transfer
  engine. Arbitrary copy failures should not silently trigger a bulk round trip.
- Keep frequently rewritten locks and other control metadata in Hot storage;
  select stable-payload tiers deliberately through provider/runtime settings.
  YABT currently sets no access tier on uploads or copies. Setting the entire
  account default to Cold also affects newly created controls. Validate the
  complete metadata/history lifecycle and costs before choosing a policy.

Normal successful incremental runs can avoid unchanged payload downloads using
the valid change manifest; they still list metadata and read control documents.
Same-length changes with different fingerprints can trigger target reads.
Measure request counts and transferred bytes for initial, unchanged, changed,
interrupted, and restored workloads in the pilot. Do not use a full 34 TB
byte-for-byte download as an unbudgeted routine acceptance step. Include a
deliberate verification/restore budget and test representative restored bytes.

[nas-retail-api]: https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices
[nas-blob-pricing]: https://azure.microsoft.com/en-us/pricing/details/storage/blobs/
[nas-access-tiers]: https://learn.microsoft.com/en-us/azure/storage/blobs/access-tiers-overview
[nas-credit-faq]: https://learn.microsoft.com/en-us/visualstudio/subscriptions/faq/subscriber/azure/
[nas-copy-blob]: https://learn.microsoft.com/en-us/rest/api/storageservices/copy-blob-from-url

#### Proposed delivery sequence

Proposed next delivery slices, pending agreement:

1. Verify runtime/native compatibility on the inspected NAS and confirm the
   selected shares, file count, largest files, upload bandwidth, and available
   scratch space. Start with mirror representation unless a folder has a clear
   reason for packaging. Normal incremental runs still traverse metadata and
   process the root manifest; they do not use a filesystem change journal.
   Completed objects survive restarts, with the costly comparison caveat above;
   partial-file/partial-ZIP upload continuation is not implemented. Size scratch
   for the largest replayed object plus indexes; nested packages may need more.
2. Package the published CLI in a production .NET runtime container and supply
   deployment/configuration examples. The existing Docker image is an SDK test
   environment. Mount source data read-only; keep credentials outside archive
   metadata and provide separate writable persistent progress/log storage and
   disk-backed scratch space. Prepare the required source descriptor/policies
   before mounting read-only. Match the container user to NAS read permissions.
3. Inventory all source shares needed to cover the complete dataset. Resolve
   any explicit exclusions with Richard rather than reducing scope silently.
   Include/exclude patterns currently warn and are ignored. Do not promise that
   Synology-generated folders, recycle bins, or other unwanted content will be
   excluded. Implement filters if an agreed selection requires them.
4. Resolve the recovery-read, large history-copy, and tier-selection gaps above.
   Validate their failure safety and transfer/request costs before the full run.
   Confirm credit eligibility, region, redundancy, history growth, and a budget
   for verification and disaster recovery. Agree production account settings.
5. Exercise the actual CLI container against an isolated prefix in
   `yabtdevhafner/archive`: initial backup, unchanged run, additions, changes,
   removals, lock renewal across a long upload, network failure, graceful stop,
   forced termination, restart with persistent progress, and restore into a
   separate filesystem directory.
   Verify restored bytes. Configure and test CLI termination and container stop
   grace together; current command-line defaults must not be assumed adequate.
6. Run a representative NAS pilot, then the full initial backup and a restore
   drill. Measure elapsed time, peak memory, scratch usage, and incremental
   request/transfer behavior. Use an external scheduler such as DSM Task
   Scheduler for periodic executions, preserve the command exit code, prevent
   overlapping jobs, and arrange failure reporting and log retention.

Source consistency and recovery scope must remain explicit. A read-only mount
does not freeze files being changed by other NAS processes. A stable Btrfs
snapshot is a possible input when supported by the actual NAS; live databases
need application-consistent preparation. Current archives preserve file bytes,
names, modification times, and empty directories, but not a complete Synology
system image, ownership/ACL/xattr state, or original link topology. Historical
bytes are retained, while selecting a historical version through restore is
still unimplemented. Whether these are prerequisites depends on the selected
data and required recovery behavior.

## Suggested Codex-Sandbox-01 sequence

1. Check out the handoff commit and read `AGENTS.md` plus this document.
   Recreate fixtures using VM-local paths; the committed launch profiles contain
   S650A-specific paths and are not VM setup instructions.
2. Establish the test baseline with
   `dotnet test yabt.sln --configuration Release --verbosity minimal` .
3. Add targeted regression tests for R01-R04, confirm failures, and address
   those safety findings first. Use only disposable data for the destructive
   reproductions.
4. R05-R09 are implemented. R08 uses deterministic coordination to verify
   cancellation ownership rather than timing-dependent sleeps.
5. I01-I03, the I04 implementation, Linux/ext4, and deployed Azure acceptance
   are complete for their documented scopes. WebDAV and pCloud are backlogged.
   Prioritize the NAS-to-Azure delivery slices above; revisit further large-tree
   optimization when representative measurements justify it. Reuse the
   committed runners for later regressions.
6. Update each finding's status with the fixing commit and verification
   evidence. Do not mark an item resolved merely because the pre-existing suite
   passes.

The findings are a review handoff, not a blanket redesign specification. Keep
fixes individually reviewable and discuss changes to durable formats, policy
semantics, or architectural requirements with Richard.
