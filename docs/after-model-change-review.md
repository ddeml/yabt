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
- Status: **R01-R06 and R09 are implemented for their documented scopes; other
  findings remain open**. Recording and committing the baseline did not implement
  the proposed fixes. R09 was added during the follow-up discussion on
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
| R07 | P2 | Unavailable restore-only entry can crash `verify-restore` | Static control-flow finding | Open |
| R08 | Investigate | Cancellation may release a lock before mutation finishes | Static concurrency concern | Open |
| R09 | P1 | Hard-linked archive files share mutable contents and distort deduplication savings | CLI reproduction and hard-link/recovery regression tests | Implemented |
| I01 | Improvement | ZIP buffers accumulate across a backup | Lifetime inspection | Open |
| I02 | Improvement | ZIP restore repeatedly parses the central directory | Inspection and isolated probe | Open |
| I03 | Improvement | Current-live restore traverses history unnecessarily | Traversal inspection | Open |
| I04 | Improvement | Synchronizer responsibilities and provider test coverage | Architectural review | Open |

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
[ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs)
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
[ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs)
, `CreateContextAsync` and `ApplyProjectionAsync` ; compare existing restore
location validation.

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
[ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs)
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

**Proposed direction:** Handle unavailable names from either side before
classifying source-only or restore-only entries.

**Acceptance checks:** Use an Information-enabled logger and restore-only
inaccessible/reparse entries. Verification must report uncompared entries,
return a nonidentical result, and avoid throwing or double-counting.

### R08: Cancellation may release ownership before mutation finishes

**Code:**
[FileSystemRestoreTarget.cs](../src/Yabt.Sync/Implementation/FileSystemRestoreTarget.cs)
, `CommitStagedFileAsync` ;
[FileSystemObjectStore.cs](../src/Yabt.FileSystem/Implementation/FileSystemObjectStore.cs)
, move operations;
[YabtTaskExtensions.cs](../src/Yabt.Common/Async/Extensions/YabtTaskExtensions.cs)
, `WaitOrAbandonAsync` .

**Static concern, not a reproduced race:** `YabtTask.Run` intentionally cancels
waiting while synchronous work can continue. A commit or move may still be
active when callers clean staging and dispose the archive lock. A subsequent
command could acquire that lock before the previous mutation finishes,
particularly with delayed filesystem I/O.

**Proposed direction:** Once an irreversible filesystem step starts, retain its
staging and lock ownership until it finishes, then observe cancellation at the
next safe boundary. Observing an abandoned exception alone does not serialize
the mutation.

**Acceptance checks:** Add a controllably delayed mutation, cancel it, and
attempt a second writer. Assert that cleanup and lock release cannot overtake
the first mutation. Preserve cancellation responsiveness during preparation and
between commits.

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

[ArchiveProjectionObjectStore.cs](../src/Yabt.Sync/Implementation/ArchiveProjectionObjectStore.cs)
retains projected objects/scopes. Their delegates retain
`ZipPackageProjectionMaterialization` in
[ZipArchiveFormatHandler.cs](../src/Yabt.Format.Zip/Implementation/ZipArchiveFormatHandler.cs)
, whose completed task retains the package byte array. Many independent ZIP
children therefore retain their combined compressed size even after upload.

Introduce explicit materialization ownership and release after the complete
consistency group is processed. Preserve sibling revalidation and
source-read-once guarantees. Consider spillable package construction separately.
Measure peak memory over many packages; the known in-memory ZIP design is a
limitation, and cumulative retention makes it broader than a per-package limit.

### I02: Avoid repeated ZIP central-directory parsing

[StagedZipRestorePackage.cs](../src/Yabt.Format.Zip/Implementation/StagedZipRestorePackage.cs)
, `OpenEntryAsync` , opens a fresh `ZipArchive` and calls `GetEntry` for each
output. Restoring N entries consequently repeats central-directory parsing with
quadratic aggregate work.

An isolated .NET 10 in-memory probe matching this pattern counted approximately
2.1/6.6/22.9 MB of reads for 100/200/400 one-byte entries, from ZIPs of only
10.9/21.8/43.6 KB. This corroborates the access-pattern cost; it is not an
end-to-end restore benchmark.

Consider a bounded pool of reusable readers per staged package, with explicit
ownership for concurrent extraction. Measure allocations and restore time while
preserving full package validation and concurrency bounds.

### I03: Prune history before current-live restore traversal

[ArchiveSynchronizer.cs](../src/Yabt.Sync/Implementation/ArchiveSynchronizer.cs)
, `LoadRestoreObjectsAsync` , recursively lists the live prefix and filters
internal paths afterward. With the default empty live prefix, this enumerates
history and staging descendants unnecessarily. Restore work grows with
accumulated history; inaccessible history can also interfere with a current-live
restore.

Use folder-local traversal and prune reserved prefixes before descending. Verify
provider call counts with a small live tree and large history tree. Evaluate
destination safety traversal separately: its deliberate safety checks must not
be removed merely to reduce scanning.

### I04: Separate orchestration responsibilities and strengthen provider tests

`ArchiveSynchronizer` is approaching 6,000 lines. Useful boundaries include
backup execution, restore planning/execution, and manifest transactions. Shared
filesystem identity, containment, and availability rules would address the
recurring assumptions behind R01-R05.

Refactor after targeted regressions establish behavior. Prefer cohesive
components with ownership of real invariants over additional forwarding
abstractions. Add provider contract tests and controlled failures around
hierarchy, moves, conditional mutations, and lock lifetime; the Azure provider
tests at review time mostly exercised configuration and client construction
rather than actual storage operations.

Related observations to evaluate during this work:

- Restore scans every planned history move before every committed file; index
  blockers by path/ancestor to avoid O(writes x history moves) checks.
- Restore path comparison is selected by operating system rather than actual
  filesystem case sensitivity. Include case-insensitive non-Windows volumes and
  case-sensitive Windows directories when portability work expands.

## Suggested Codex-Sandbox-01 sequence

1. Check out the handoff commit and read `AGENTS.md` plus this document.
   Recreate fixtures using VM-local paths; the committed launch profiles contain
   S650A-specific paths and are not VM setup instructions.
2. Establish the test baseline with
   `dotnet test yabt.sln --configuration Release --verbosity minimal` .
3. Add targeted regression tests for R01-R04, confirm failures, and address
   those safety findings first. Use only disposable data for the destructive
   reproductions.
4. R05 and R06 are implemented; resolve R07 next. Investigate R08 with
   deterministic coordination rather than timing-dependent sleeps.
5. Measure I01-I03, then refactor under the resulting tests and provider
   contracts.
6. Update each finding's status with the fixing commit and verification
   evidence. Do not mark an item resolved merely because the pre-existing suite
   passes.

The findings are a review handoff, not a blanket redesign specification. Keep
fixes individually reviewable and discuss changes to durable formats, policy
semantics, or architectural requirements with Richard.
