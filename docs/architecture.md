# Architecture

YABT separates durable archive truth from runtime convenience.

The durable state is:

- Root archive metadata such as `.yabt-root.json`.
- The root change manifest, stored by default as Brotli-compressed
  `.yabt-change-manifest.json.br` , used for fast live-state comparison.
- The restored filesystem's `.yabt-logical-state-manifest.json` , used as
  rebuildable quick evidence for later restores.
- Per-folder metadata such as `.yabt-policy.json`.
- Human-readable manifests next to package artifacts.
- Embedded manifests inside package artifacts.
- Standard archive files when packaging is enabled.
- Object-store paths laid out in a directly browsable hierarchy.

Runtime state is:

- Command-line options.
- Runtime credentials.
- Temporary files used while creating packages.
- Scan or reconciliation working data held only for the current command.
- Optional per-command diagnostic log files stored outside synchronized roots.

Do not introduce a metadata cache initially. If a cache is added later, it must
remain disposable and rebuildable from durable metadata, manifests, and
object-store contents.

## Configuration Diagnostics

Unknown optional settings warn and are ignored. Recognized but unimplemented
optional settings warn with the effective behavior. Invalid JSON and known
values, unsupported required formats/providers, directory-link approvals, and
integrity metadata retain strict validation. Azure descriptor declarations
retain their credential-safe allowlist, even for unused stores.

Metadata readers identify unknown root, layout, and policy properties. Providers
validate their selected descriptor extensions. Runtime options use the standard
.NET options binder with a diagnostic inspection of their registered section;
this also covers custom Azure configuration paths and option reloads. Unused
sections and arbitrary host configuration are outside that inspection.

`IArchiveFormatHandler.ValidateBackupPolicy` owns diagnostics for active source
settings, including nested mirror folders that do not invoke a separate format
projection. The built-in handlers warn that include/exclude filters and
per-folder options are ignored. ZIP continues to use its runtime compression
options. Restore treats archived policies as payload without activating them.

`ConfigurationWarnings` shares a concurrent warning set within one asynchronous
operation. Each source/property pair logs once, identifying the file or section
and property path without setting values. Policy source provenance is transient
and does not enter canonical manifests or affect projection identities.

## Object Stores

YABT treats backup, restore, and reconciliation locations as object stores. A
plain filesystem, Azure Blob Storage, and WebDAV are all peers behind the same
object-store abstraction.

The object-store abstraction provides raw access to the underlying store. It
exposes ordinary operations such as reading, writing, copying, moving objects,
moving complete folders or prefixes, and folder-local traversal. Filesystem and
WebDAV map a complete folder move to their native folder or collection
operation; Azure Blob maps it to all objects under the exact prefix. A complete
folder move includes hidden marker objects and native empty descendants and must
not merge with an existing destination. The abstraction must not know what
`live` or `hist` mean, and it must not decide archive historization behavior.

Traversal is hierarchical: callers ask for the files and immediate child folders
under a folder prefix. Filesystem and WebDAV providers can expose native folders
directly. Providers without real folders, such as Azure Blob Storage, emulate
folders from object-name prefixes.

The current `mirror` projection represents every empty folder with the reserved
zero-byte `.yabt-empty` marker object, including on filesystem targets. The
object-only projection contract has no native folder-creation operation, so the
marker is the portable durable representation of that folder. It remains live
while the folder is empty and moves to history when the folder gains content or
changes format. Providers hide it from normal YABT traversal, although ordinary
filesystem, Azure, or WebDAV browsers may display it. The marker is YABT folder
plumbing rather than ordinary source data or temporary cleanup residue.

Initial object store providers:

- `fileSystem`
- `azureBlob`
- `webDav`

Operation direction determines whether a store is acting as the source, target,
backup location, restore location, or reconciliation peer. The durable archive
format should not depend on Azure-specific features.

## Archive Layout

The root descriptor maps logical archive branches to physical object prefixes:

- `livePrefix`: where the current logical state is projected.
- `histPrefix`: where replaced or deleted historical state is preserved.

The default layout is an inline layout:

```json
{
  "livePrefix": "",
  "histPrefix": ".yabt-hist"
}
```

This makes an ordinary source folder usable as the logical live branch without
forcing all data under a `live` child folder. Archive-style roots may still
choose explicit branch directories:

```json
{
  "livePrefix": "live",
  "histPrefix": "hist"
}
```

The synchronizer owns historization. Before replacing or removing logical live
objects, it should preserve the old representation under the configured history
prefix. When a complete live folder becomes obsolete, the synchronizer moves
that folder representation to history as a unit instead of moving its visible
objects and then deleting the leftover container. This preserves `.yabt-empty` ,
native empty descendants, and unexpected contents if a run was interrupted. The
exact historical sublayout may evolve, but it should remain inspectable.

The validated source `.yabt-root.json` is reserved root metadata rather than a
live projected object. Backup carries its exact bytes to the archive outside
`livePrefix` and outside packages, and historizes a prior different copy only
when its archive id and layout agree. A schema-version-3 live change manifest
binds those bytes by xxHash128 and length. Restore uses the archived descriptor
and layout as authoritative and writes the same bytes to the filesystem
destination; it intentionally does not rewrite stale machine-specific paths.

History deduplication is intentionally separate from backup. The `deduplicate`
maintenance command scans only the history branch, finds content-hash candidates
above the configured tiny-file threshold, and confirms every replacement with a
complete byte comparison. This keeps the ordinary backup path independent of
history size.

The history manifest is a durable, rebuildable catalog separate from the live
change manifest. It describes every historical occurrence and records whether
its bytes are materialized or replaced by an explicit JSON reference. It is not
authoritative by itself: materialized entries use only information observable by
scanning or opening their actual objects, and self-contained references repeat
their exact entries. Rebuilding may require reading and hashing materialized
content, but losing the catalog does not lose exclusive metadata.

Each duplicate group keeps one stable materialized copy in history. A reference
identifies that content by its xxHash128 content hash instead of naming another
reference, so references never form chains. The current command does not scan
live objects; future resolution may consider them as optional extra candidates,
but they are neither modified nor required to remain available for historical
restoration.

Materialized history backing must be independent of other object names. The
provider-owned `IsIndependentMaterializationAsync` check supplies transient
evidence separately from durable history metadata. The filesystem provider
reads file identity and hard-link count from an opened handle; a missing or
unsupported identity/count is an error. Azure, WebDAV, and memory stores retain
their independent-key resource model. The supported WebDAV operations do not
expose hidden server-side filesystem aliases.

Deduplication keeps multiply linked filesystem objects materialized, excludes
them from backing selection and redundant-object deletion, and claims no
storage savings for them. All existing references require verified independent
backing, including tiny or otherwise excluded content and orphan references
awaiting cleanup. A shared orphan reference cannot be deleted by cleanup.
Backing hashes and independence are checked before publication and again before
deletions; each removed object's independence is rechecked before its guarded
deletion. An observed later change fails with original bytes and recovery
markers retained. YABT does not silently break hard links or repair shared
history copies.

Source hard links remain ordinary logical files, producing independent archive
and restore copies. The filesystem provider rejects multiply linked control
files before in-place use, checks an existing archive lock before delete-on-close
opening, and validates the acquired handle again before truncation. Restore
validates staging before timestamp updates and commits. Atomic replacement of
one directory entry remains safe because it does not change the contents behind
other hard-link names. Native Windows/Linux evidence is required for these
writable safety checks; ordinary source reads remain unaffected.

Deduplication is ordered for recoverability. YABT verifies the materialized
backing copy, writes and commits the complete reference metadata, and only then
removes redundant bytes. A failure may leave extra materializations, but it must
not leave an unresolvable reference. Archive-mutating commands require
coordination so backup and deduplication cannot concurrently change the same
history.

The history catalog has its own invalidation marker. Backup writes the marker
before making the catalog stale. If backup changes history successfully, it
deletes the stale catalog and then the marker, so neither remains after the
command. Deduplication uses the marker while rebuilding and removes it only
after safely publishing the catalog and completing guarded deletions. A marker
that remains after either command indicates interruption. Archive locks
coordinate YABT writers; external tools must not modify an archive during a
mutating command.

## Archive Format Handlers

Folder representation is selected by a string `format` value in folder metadata.
The format value is owned by the provider that implements it.

Initial archive format handlers:

- `mirror`
- `zip`

`mirror` stores files individually. `zip` stores a logical folder as a package
artifact plus adjacent metadata. Future providers such as `7z` or `tar.gz` may
be added without changing `Yabt.Core` .

An archive format handler owns both directions of its representation.
`ProjectBackupAsync` transforms a source folder and policy into intended archive
objects. `ProjectRestoreAsync` receives the complete artifact group for one
projection and reverses it into logical files, native directories, and possibly
more provenanced artifacts. A handler validates portable representation details
such as ZIP entry paths and links, but it does not compare destination state or
decide historization. The bidirectional contract is `IArchiveFormatHandler` .

When a subfolder uses a packaging format such as `zip` , its projected package
artifacts replace the source folder entry and are composed into the logical
parent folder. A `mirror` projection continues to preserve the source folder
hierarchy. Packaging the selected source root places its artifacts at the target
live root.

Handlers stream backup objects from `ProjectBackupAsync` . Formats that can emit
objects incrementally, such as `mirror` , should do so. Formats that need
complete folder knowledge, such as `zip` , may collect their source folder first
and then yield the finished package and adjacent manifest. Restore projections
use the same `ArchiveProjectedObject` model for desired files and may own
disposable staging. The synchronizer groups artifacts by their logical path,
format, format version, and projection id, invokes the owning handler, and
recursively processes any provenanced artifacts it emits. This turns a ZIP
nested inside another ZIP back into its original logical folder without guessing
from a filename.

Store readiness and destination initialization are separate operations.
`IReadOnlyObjectStore.EnsureReadyAsync` validates an existing readable root
without modifying storage. Filesystem readiness attempts directory enumeration;
Azure reads container properties and WebDAV checks the root collection.
`IObjectStore.InitializeAsync` may create root resources for a mutating backup.
Source projection, verification, and dry runs never initialize storage. An
absent filesystem target can still be compared as prospective empty storage
during a dry run; this does not make a missing source valid. Deduplication
validates the existing archive in both modes. Descriptor validation remains in
the metadata and synchronization layers, outside provider readiness.

Readiness cannot prevent a later source change. Source traversal must report
failed listings and reads rather than infer an empty branch. Backup suppresses
unrelated removals and successful manifest publication after source read
failures, while retaining safe checkpoints already written.

Object keys preserve exact name components, including whitespace. An empty
string denotes the root; a whitespace-only name remains a distinct object.
`ObjectPath` represents canonical keys with ordinal identity and `/` separators.
It rejects rooted paths, drive prefixes, empty components, `.`/`..`, backslashes,
and NUL. Native path selectors are converted at the filesystem boundary. Native
names containing a literal backslash cannot be represented
and are refused instead of being interpreted as additional folders. Providers
also reject names their destination cannot represent exactly, including Windows
trailing-space/dot aliases and device names. WebDAV encodes each exact component
in the URI and validates decoded listing components. Restore preflight checks
both desired paths and existing destination entries before mutation.

`IObjectStoreNamespace` owns representability and destination comparison rules.
Its scoped tree validator includes implicit parent directories, so distinct
logical paths cannot silently merge under a provider's case or accent rules.
The filesystem implementation queries native directory semantics rather than
assuming all Windows volumes ignore case or all Unix volumes are ordinal.
Unsupported comparison semantics fail explicitly. Shared synchronization and
format projection code use canonical names and provider capabilities.

The initial native backends query Windows directory case-sensitivity flags and
accept Linux ext/F2FS without casefold, btrfs, and tmpfs. Other Unix or remote
filesystem semantics require provider support before namespace validation can
approve them. Windows insensitive comparisons use ordinal case folding; native
access also checks existing spelling so aliases cannot overwrite another
logical name if native Unicode equivalence differs.
Provider-owned root `.yabt-tmp` plumbing retains its existing case-variant
handling; that exception does not apply to live data names.

Backup snapshots projected path metadata for namespace preflight before live
reconciliation; payload openers remain lazy. Restore validates its complete
expanded plan before committing files. Mapping failures use ordinary contextual
YABT exceptions and the existing failure/recovery path. They do not authorize
renaming, skipping desired content, or treating it as removed. Later I/O failures
retain already committed safe checkpoints; operations are not whole-tree
transactions.

Backup plans file/directory transitions and provider-equivalent spelling changes
before opening replacement payloads. It validates the desired layout through
`IObjectStoreNamespace.AfterRemoving` , then historizes the confirmed blockers.
The prospective namespace models recreated directories using their surviving
parent's comparison rules; existing directories retain their own rules. Normal
provider access still enforces exact existing spelling. Entire blocking folders
move with their hidden markers and native empty descendants. Flat providers keep
an exact object separate from its same-named descendant prefix.

Dry runs and verification use empty virtual subtrees for planned folder moves,
so they do not probe a blocked destination or mutate the archive. Interrupted
backup can leave live incomplete while preserving previous bytes in history.
Rerunning reconciles the source's current readable state; there is no frozen
snapshot or automatic rollback. Normal restore still requires a valid live
manifest. Unrelated obsolete entries remain deferred until desired writes
succeed.

`IArchiveLocationResolver` prepares the source store, selected canonical prefixes,
policy reads, and native overlap checks. `IRestoreDestination` owns directory
creation and commits from opaque staging tokens. Physical restore paths and
temporary-file operations belong to the filesystem provider. Backup comparison
replay remains private temporary storage, outside archive `.yabt-tmp`.

Backup asks the location resolver for bounded overlap validation before target
initialization or locking. The filesystem provider compares the effective
selected source subtree with the whole target root, whose history, metadata,
and staging may also be mutated. It separately protects source descriptor and
initial policy read locations. A selected source folder and archive may be
siblings below the same descriptor root.

Preflight examines existing path components, resolving known junctions and
symbolic links with a bounded number of substitutions and preserving missing
target tails. It uses directory-specific namespace comparison and performs no
additional tree scan, payload read, remote service probe, or source marker
write. Identically spelled components need no comparison probe; unsupported
ancestor comparison semantics leave overlap undetermined. Ordinary I/O failures
still propagate, and destination namespace validation remains strict. Current
source location resolution is filesystem-based; targets from other providers
are undetermined. Unknown aliases and cross-provider mappings remain permitted,
so a successful preflight does not prove disjoint storage.

Existing source traversal supplies further overlap evidence during normal
access. A provider reports known overlap through `ObjectStoreOverlapException`
inside its contextual failure. The synchronizer and ZIP materialization stop
immediately on that evidence, even through nested or aggregate exceptions,
instead of continuing with other payloads. Prior safe checkpoints and recovery
markers remain; unrelated removals and successful manifest publication do not
run. Normal cleanup, such as releasing the archive lock, still occurs. This is
best-effort detection, not a filesystem snapshot or protection against concurrent
external topology changes.

Filesystem access validates every existing descendant component, including
ancestors of an otherwise ordinary file or directory. Mutable archive and
restore-destination access rejects descendant links, while a configured store
root may itself be linked. Source reads may follow a directory link only when
its containing `.yabt-policy.json` explicitly names that child and its expected
target. Permissions are source-only and never inherited by archive writes or
activated by restored policies; there is no CLI bypass. Unapproved links cause
an error instead of silently disappearing from the desired archive state.

The filesystem source reconstructs physical directory ancestry from the logical
root-relative path for each access. This keeps cycle checks effective across
folder-local calls, independently scheduled branches, and nested ZIP
projections. A repeated ancestor is a cycle, while the same directory reached
through separate source branches remains distinct logical content. Approved
targets must not overlap the selected filesystem archive, the source history
prefix, or `.yabt-tmp` . Physical identities are transient validation evidence.
Mutable linked subtrees require a future ownership and storage design that
preserves archive locking, atomic staging, and history independence.

The `mirror` handler maps source files one-to-one in both directions and
reverses `.yabt-empty` marker objects into native directories. The ZIP handler
maps a source folder to a package plus
`<complete-package-name>.yabt-manifest.json` . It embeds the exact same manifest
bytes at `.yabt-package-manifest.json` inside the ZIP. The versioned manifest
contains the root-relative logical source path, canonical policy snapshot, exact
entry timestamps and content hashes, and nested projection provenance. Its
external copy lets restore plan without opening the package; before any needed
ZIP payload is written, the handler validates the complete package and requires
the embedded copy to match byte-for-byte. Directory-only packages are validated
eagerly before mutation. Exact timestamps therefore do not depend on the lossy,
1980-through-2107 ZIP DOS field.

Artifacts carrying the same projection provenance are reconciled as one
consistency unit. If one member needs creation or replacement, backup
revalidates siblings that matched only quick evidence against the handler's same
materialized snapshot. This prevents a successful archive from pairing an older
ZIP with a newer adjacent manifest when source files change during a run.

Package artifact names use a deterministic, algorithm-tagged full xxHash128
logical-representation hash and exclude per-run creation time, so an unchanged
projection retains the same live key. The hash token is lowercase unpadded
Base32hex in file names so it remains stable on case-insensitive file systems;
JSON uses the shorter unpadded Base64URL form. ZIP identity includes the
root-relative logical path, canonical policy, ordered relative paths, archived
lengths and modification times, per-object change fingerprints, nested
provenance, and output-affecting compression settings. The root change manifest
separately binds the actual package and adjacent-manifest bytes; a package
manifest cannot embed its own finished package hash without creating a cycle.

`backup` and `restore` enter one direction-selected operation boundary, then use
typed direction-specific preparation and application pipelines. They share
bidirectional handlers, desired-object reconciliation, and historization. Backup
consumes reconciliation results incrementally so at most one replay object is
staged. Restore completes metadata, path-safety, and reconciliation preflight
globally, then prepares final files concurrently and commits each validated file
independently.

Projection provenance is carried both in schema-version-3 root change-manifest
entries and in nested package-manifest entries. Restore reverses these groups
recursively, while a similarly named ordinary `.zip` entry without provenance
remains an ordinary file.

## Project Boundaries

`Yabt.Core` contains durable archive concepts and cross-platform abstractions.
It should not contain Azure, WebDAV, CLI, or provider-specific format logic.

`Yabt.Common` contains shared cross-cutting primitives that should not pull in
provider dependencies.

`Yabt.Metadata` handles JSON formats such as `.yabt-root.json` ,
`.yabt-policy.json` , the live change manifest, and package manifests.

Format handler projects implement bidirectional archive representations such as
`mirror` and `zip` .

Object-store provider projects adapt storage systems such as the filesystem,
Azure Blob Storage, and WebDAV.

`Yabt.Sync` coordinates root metadata loading, policy evaluation, format handler
resolution, layout mapping, historization, backup, restore, verification,
reconciliation, and explicit history deduplication.

`Yabt.Cli` provides the command entry point.

## Change Detection

The archive root contains a logical JSON live change manifest. It is stored as
Brotli-compressed `.yabt-change-manifest.json.br` by default or as plain
`.yabt-change-manifest.json` when configured with
`changeManifestCompression: "none"` . Current writers use schema version 3. The
document records `rootFormat` , optional root-descriptor content hash and length
evidence, and every live artifact's path, logical change fingerprint,
actual-byte xxHash128 hash, optional artifact length, and optional projection
provenance. Provenance contains a root-relative logical path, format, positive
format version, projection id, and artifact role so packages and sidecars can be
grouped without inference. Ordinary files do not repeat their source length or
modification time outside the readable fingerprint. An optional `artifactLength`
is recorded only when the handler cannot supply the produced object's length,
such as for a lazily built ZIP, so quick verification can still detect target
truncation. The decompressed document is deterministic, ordered by live-relative
path, human-readable, and protected by its own xxHash128 self-hash.

Readers retain explicit compatibility for schema versions 1 and 2. Version 1 has
no root-format evidence and can be restored only when its representation is
unambiguous. Version 2 adds `rootFormat` but predates descriptor evidence and
provenance. A successful current backup rewrites either older logical document
as version 3.

For ordinary files, the versioned quick fingerprint stores the known length and
exact modification time converted to UTC directly as
`stat-v1:<UTC timestamp>:<length>` . This is compact, canonical, and
human-readable, but it is not proof of file contents. A same-length edit whose
modification time is preserved, or same-length target corruption, can pass a
quick check. YABT uses xxHash128 for actual-byte content hashes and aggregate
ZIP identities. It is chosen for fast non-adversarial change detection; use
byte-for-byte mode when complete content comparison is required.

Normal `backup` and `verify` use matching fingerprints plus the prior live
change manifest to avoid opening unchanged source and target objects. A missing
fingerprint, missing reliable target length, missing manifest, or changed
fingerprint falls back to stream comparison. `backup --byte-for-byte` and
`verify --byte-for-byte` bypass the quick match and compare complete streams.
The byte-for-byte mode is the integrity check; the default verify result is
explicitly a quick metadata check. `sync` remains a compatibility alias for
`backup` .

When a mutating backup must compare streams, it copies the source bytes it is
already comparing into a private delete-on-close local replay file. If the
comparison finds a change, YABT finishes that one source read and uploads the
captured snapshot instead of reopening the source. This both avoids duplicate
source I/O and prevents a file changed between comparison and upload from
producing a different archived version. Objects are currently processed
sequentially, so local temporary storage must hold at most the largest projected
object being compared. Verify and dry-run operations do not create replay files
because they never upload. ZIP follows the same one-read rule: when an entry
lacks enough metadata or a provider hash, its bytes are hashed while that same
read is written into the package. ZIP package construction attempts every
independent source entry and aggregates source-read failures. An eagerly built
fallback package retains one spillable backing for replay. Nested packages with
incomplete metadata can retain several temporary files while sharing the
configured memory budget.
Backup projections own their materializations explicitly. Sibling consistency
checks complete when the last artifact in a group is processed; that group and
its nested scopes then release their backing. Failure and cancellation also
release projection ownership after started materialization has finished.

Large manifests are streamed into operation-owned SQLite entry collections.
Canonical sorting and lookup use disk indexes; hashing consumes canonical JSON
incrementally. Serialization preserves the durable JSON/Brotli representation.
A manifest model owns its replayable entries and must be disposed. Sibling
manifests and nested projections share an operation index with bounded page
caches; SQLite files contain no exclusive or durable archive evidence.

ZIP creation streams payloads and spools central-directory records, then writes
standard ZIP64 end records. Restore stages package bytes through temporary
content, parses its central directory once, and retains offsets in the temporary
index. Independently positioned entry streams validate local headers, CRC and
length without rebuilding the directory. No reader pool or per-entry copy of
the central directory is required. General synchronization plans and individual
metadata records can still consume memory; the payload budget is not a total
process-memory guarantee.

Backup classifies source-object open and stream-read failures separately from
target-provider or mutation failures. It continues reconciliation after
independent source read failures and returns a deterministic end-of-run report
with every source path and underlying reason. Ordinary same-path replacements
retain their readable source snapshot before historization. Confirmed shape or
spelling blockers may already have moved to history before payload preparation.
If any source read fails, unrelated target extras, root-descriptor replacement,
history finalization, and live change-manifest publication are deferred. Any
successful readable-object writes remain rerun checkpoints and invalidation
evidence remains active where required. Cancellation, enumeration, target
reads/writes, locks, and historization failures remain fail-fast.

Filesystem mutation workers retain their caller's ownership until they finish.
The provider schedules them with standard .NET tasks rather than abandoning the
wait through `YabtTask.Run` . Cancellation can prevent a worker from starting;
once started, its actual result or failure reaches the caller before staging
cleanup or archive-lock release. Successful commits complete their ownership
bookkeeping before the next operation observes cancellation. Conditional-lock
waits and content hashing remain cooperatively cancellable. A stalled native
filesystem call can therefore delay command cancellation. Read-only worker
abandonment and remote-provider request semantics remain separate concerns.

The configured compression controls only what a mutating backup writes. Readers
always inspect both supported filenames. If both exist, they are trusted only
when both validate and have the same logical self-hash; a corrupt or conflicting
pair forces full comparison. Before discarding multiple untrusted
representations, backup creates `.yabt-change-manifest.invalid` . Its presence
prevents a partially completed cleanup from leaving one stale manifest that
appears trustworthy. The marker remains until the replacement manifest is safely
written, then is deleted last. A successful mutating backup leaves exactly the
configured representation live. Obsolete root change manifests and the root
invalidation marker are deleted, never historized.

Target-native modification times are not compared with source times because
uploads and remote servers assign different target times. The manifest preserves
the source-derived fingerprint instead. Missing source timestamps do not receive
a fake quick fingerprint; formats may use a separate deterministic timestamp
only where an encoding such as ZIP requires one.

The old manifest is deleted before the first live mutation and the replacement
is written last. An interrupted run therefore leaves no trusted live manifest
and the next backup performs full comparisons. A mutating backup can discard and
rebuild an invalid manifest; byte-for-byte mode can proceed without trusting it.
Current MVP behavior does not search history for manifest metadata left there by
older builds.

The current manifest is root-wide. It removes repeated byte scanning but still
requires metadata enumeration and rewrites the JSON document when it changes.
Folder-local or sharded manifests, filesystem event monitoring, Synology btrfs
snapshot diffs, and other scalable delta sources remain future improvements
behind change-detection abstractions.

## Restore Symmetry

Current-live restore enumerates the archive one folder at a time. It prunes
history and staging prefixes before descending, including when the live prefix
is empty. A nested history prefix still permits traversal of its ancestors and
live siblings. Reserved metadata filenames exclude exact objects; they do not
hide descendants of a same-named virtual folder. Manifest coverage checks and
hidden empty-folder-marker lookup remain in effect. Provider validation during
parent listing and the separate destination safety traversal still apply.

Restore uses the same durable metadata that backup creates. The live change
manifest records the root format and projection groups, package manifests
describe each reversible ZIP representation, and the archived root descriptor
supplies the authoritative archive identity and layout. Final files flow through
a completion-order pipeline bounded by `Sync:RestoreMaximumConcurrency` ,
default `5` . Each new or changed file is written once to a unique staging file
under the destination's reserved `.yabt-tmp` while its length and xxHash128
value are validated. Only then does restore historize that file's blocking live
representation and atomically rename the staging file into place.
Format-projection staging is released after its last dependent output is
prepared. Unrelated extras are historized after all desired files succeed.

This per-file transaction is the restore checkpoint. Archive-file and
destination-file read failures are collected across the bounded pipeline and
returned as one deterministic path-and-reason report. Earlier committed files
remain live, each failing file's previous representation remains untouched,
unrelated extras remain live, and invalidation markers keep incomplete metadata
untrusted. No logical-state manifest is published until every required file
succeeds. The next run hashes uncertain destination files and skips those that
already match instead of downloading them again. Temporary final-output storage
is bounded by the configured in-flight files rather than the sum of all changed
data. Commit, staging, historization, mutation-lock, and metadata-write failures
remain fail-fast because continuation would be unsafe.

After a successful restore, the filesystem root contains a separate plain
`.yabt-logical-state-manifest.json` . Its self-hashed entries bind each logical
file path's current canonical `stat-v1` fingerprint to the desired actual-byte
content hash. On a later normal restore, a matching stat fingerprint and desired
hash allow YABT to leave that file untouched without opening it. Missing,
invalid, conflicting, or invalidated evidence falls back to hashing the
destination file. `restore --byte-for-byte` bypasses this shortcut; unchanged
outputs are streamed through validation while changed outputs use the same
single staging read that will be committed. Restore uses
`.yabt-logical-state-manifest.invalid` as a transaction marker, publishes
rebuilt evidence after the live filesystem is correct, and removes the marker
last.

The exact archived `.yabt-root.json` is installed when absent. An exact
destination copy is left untouched. A different copy with the same archive id
and layout requires `--replace-root-descriptor` and is historized before
replacement; an incompatible archive id or layout is refused. YABT never
rewrites machine-specific paths automatically, so the user edits obsolete
filesystem locations after restore. Any destination live change manifest is
invalidated and removed when restore changes live state or root metadata because
it would otherwise describe the prior archive projection. Restoring a specific
logical history version remains future work.

Restore must not require a cache or database. JSON metadata, manifests, package
artifacts, and object-store paths remain sufficient.
