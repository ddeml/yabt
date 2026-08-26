# Architecture

YABT separates durable archive truth from runtime convenience.

The durable state is:

- Root archive metadata such as `.yabt-root.json`.
- The root change manifest, stored by default as Brotli-compressed `.yabt-change-manifest.json.br`, used for fast live-state comparison.
- The restored filesystem's `.yabt-logical-state-manifest.json`, used as rebuildable quick evidence for later restores.
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

Do not introduce a metadata cache initially. If a cache is added later, it must remain disposable and rebuildable from durable metadata, manifests, and object-store contents.

## Object Stores

YABT treats backup, restore, and reconciliation locations as object stores. A plain filesystem, Azure Blob Storage, and WebDAV are all peers behind the same object-store abstraction.

The object-store abstraction provides raw access to the underlying store. It exposes ordinary operations such as reading, writing, copying, moving objects, moving complete folders or prefixes, and folder-local traversal. Filesystem and WebDAV map a complete folder move to their native folder or collection operation; Azure Blob maps it to all objects under the exact prefix. A complete folder move includes hidden marker objects and native empty descendants and must not merge with an existing destination. The abstraction must not know what `live` or `hist` mean, and it must not decide archive historization behavior.

Traversal is hierarchical: callers ask for the files and immediate child folders under a folder prefix. Filesystem and WebDAV providers can expose native folders directly. Providers without real folders, such as Azure Blob Storage, emulate folders from object-name prefixes.

The current `mirror` projection represents every empty folder with the reserved zero-byte `.yabt-empty` marker object, including on filesystem targets. The object-only projection contract has no native folder-creation operation, so the marker is the portable durable representation of that folder. It remains live while the folder is empty and moves to history when the folder gains content or changes format. Providers hide it from normal YABT traversal, although ordinary filesystem, Azure, or WebDAV browsers may display it. The marker is YABT folder plumbing rather than ordinary source data or temporary cleanup residue.

Initial object store providers:

- `fileSystem`
- `azureBlob`
- `webDav`

Operation direction determines whether a store is acting as the source, target, backup location, restore location, or reconciliation peer. The durable archive format should not depend on Azure-specific features.

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

This makes an ordinary source folder usable as the logical live branch without forcing all data under a `live` child folder. Archive-style roots may still choose explicit branch directories:

```json
{
  "livePrefix": "live",
  "histPrefix": "hist"
}
```

The synchronizer owns historization. Before replacing or removing logical live objects, it should preserve the old representation under the configured history prefix. When a complete live folder becomes obsolete, the synchronizer moves that folder representation to history as a unit instead of moving its visible objects and then deleting the leftover container. This preserves `.yabt-empty`, native empty descendants, and unexpected contents if a run was interrupted. The exact historical sublayout may evolve, but it should remain inspectable.

The validated source `.yabt-root.json` is reserved root metadata rather than a live projected object. Backup carries its exact bytes to the archive outside `livePrefix` and outside packages, and historizes a prior different copy only when its archive id and layout agree. A schema-version-3 live change manifest binds those bytes by xxHash128 and length. Restore uses the archived descriptor and layout as authoritative and writes the same bytes to the filesystem destination; it intentionally does not rewrite stale machine-specific paths.

History deduplication is intentionally separate from backup. The `deduplicate` maintenance command scans only the history branch, finds content-hash candidates above the configured tiny-file threshold, and confirms every replacement with a complete byte comparison. This keeps the ordinary backup path independent of history size.

The history manifest is a durable, rebuildable catalog separate from the live change manifest. It describes every historical occurrence and records whether its bytes are materialized or replaced by an explicit JSON reference. It is not authoritative by itself: materialized entries use only information observable by scanning or opening their actual objects, and self-contained references repeat their exact entries. Rebuilding may require reading and hashing materialized content, but losing the catalog does not lose exclusive metadata.

Each duplicate group keeps one stable materialized copy in history. A reference identifies that content by its xxHash128 content hash instead of naming another reference, so references never form chains. The current command does not scan live objects; future resolution may consider them as optional extra candidates, but they are neither modified nor required to remain available for historical restoration.

Deduplication is ordered for recoverability. YABT verifies the materialized backing copy, writes and commits the complete reference metadata, and only then removes redundant bytes. A failure may leave extra materializations, but it must not leave an unresolvable reference. Archive-mutating commands require coordination so backup and deduplication cannot concurrently change the same history.

The history catalog has its own invalidation marker. Backup writes the marker before making the catalog stale. If backup changes history successfully, it deletes the stale catalog and then the marker, so neither remains after the command. Deduplication uses the marker while rebuilding and removes it only after safely publishing the catalog and completing guarded deletions. A marker that remains after either command indicates interruption. Archive locks coordinate YABT writers; external tools must not modify an archive during a mutating command.

## Archive Format Handlers

Folder representation is selected by a string `format` value in folder metadata. The format value is owned by the provider that implements it.

Initial archive format handlers:

- `mirror`
- `zip`

`mirror` stores files individually. `zip` stores a logical folder as a package artifact plus adjacent metadata. Future providers such as `7z` or `tar.gz` may be added without changing `Yabt.Core`.

An archive format handler owns both directions of its representation. `ProjectBackupAsync` transforms a source folder and policy into intended archive objects. `ProjectRestoreAsync` receives the complete artifact group for one projection and reverses it into logical files, native directories, and possibly more provenanced artifacts. A handler validates portable representation details such as ZIP entry paths and links, but it does not compare destination state or decide historization. The bidirectional contract is `IArchiveFormatHandler`.

When a subfolder uses a packaging format such as `zip`, its projected package artifacts replace the source folder entry and are composed into the logical parent folder. A `mirror` projection continues to preserve the source folder hierarchy. Packaging the selected source root places its artifacts at the target live root.

Handlers stream backup objects from `ProjectBackupAsync`. Formats that can emit objects incrementally, such as `mirror`, should do so. Formats that need complete folder knowledge, such as `zip`, may collect their source folder first and then yield the finished package and adjacent manifest. Restore projections use the same `ArchiveProjectedObject` model for desired files and may own disposable staging. The synchronizer groups artifacts by their logical path, format, format version, and projection id, invokes the owning handler, and recursively processes any provenanced artifacts it emits. This turns a ZIP nested inside another ZIP back into its original logical folder without guessing from a filename.

Filesystem traversal does not descend into directory reparse points or symbolic-link directories below the configured store root. This prevents a destructive history operation from following a link outside the archive. Configure the linked destination itself as the store root when that location is intentional.

The `mirror` handler maps source files one-to-one in both directions and reverses `.yabt-empty` marker objects into native directories. The ZIP handler maps a source folder to a package plus `<complete-package-name>.yabt-manifest.json`. It embeds the exact same manifest bytes at `.yabt-package-manifest.json` inside the ZIP. The versioned manifest contains the root-relative logical source path, canonical policy snapshot, exact entry timestamps and content hashes, and nested projection provenance. Its external copy lets restore plan without opening the package; before any needed ZIP payload is written, the handler validates the complete package and requires the embedded copy to match byte-for-byte. Directory-only packages are validated eagerly before mutation. Exact timestamps therefore do not depend on the lossy, 1980-through-2107 ZIP DOS field.

Artifacts carrying the same projection provenance are reconciled as one consistency unit. If one member needs creation or replacement, backup revalidates siblings that matched only quick evidence against the handler's same materialized snapshot. This prevents a successful archive from pairing an older ZIP with a newer adjacent manifest when source files change during a run.

Package artifact names use a deterministic, algorithm-tagged full xxHash128 logical-representation hash and exclude per-run creation time, so an unchanged projection retains the same live key. The hash token is lowercase unpadded Base32hex in file names so it remains stable on case-insensitive file systems; JSON uses the shorter unpadded Base64URL form. ZIP identity includes the root-relative logical path, canonical policy, ordered relative paths, archived lengths and modification times, per-object change fingerprints, nested provenance, and output-affecting compression settings. The root change manifest separately binds the actual package and adjacent-manifest bytes; a package manifest cannot embed its own finished package hash without creating a cycle.

`backup` and `restore` enter one direction-selected operation boundary, then use typed direction-specific preparation and application pipelines. They share bidirectional handlers, desired-object reconciliation, and historization. Backup consumes reconciliation results incrementally so at most one replay object is staged, while restore retains its complete write preflight before any destination mutation.

Projection provenance is carried both in schema-version-3 root change-manifest entries and in nested package-manifest entries. Restore reverses these groups recursively, while a similarly named ordinary `.zip` entry without provenance remains an ordinary file.

## Project Boundaries

`Yabt.Core` contains durable archive concepts and cross-platform abstractions. It should not contain Azure, WebDAV, CLI, or provider-specific format logic.

`Yabt.Common` contains shared cross-cutting primitives that should not pull in provider dependencies.

`Yabt.Metadata` handles JSON formats such as `.yabt-root.json`, `.yabt-policy.json`, the live change manifest, and package manifests.

Format handler projects implement bidirectional archive representations such as `mirror` and `zip`.

Object-store provider projects adapt storage systems such as the filesystem, Azure Blob Storage, and WebDAV.

`Yabt.Sync` coordinates root metadata loading, policy evaluation, format handler resolution, layout mapping, historization, backup, restore, verification, reconciliation, and explicit history deduplication.

`Yabt.Cli` provides the command entry point.

## Change Detection

The archive root contains a logical JSON live change manifest. It is stored as Brotli-compressed `.yabt-change-manifest.json.br` by default or as plain `.yabt-change-manifest.json` when configured with `changeManifestCompression: "none"`. Current writers use schema version 3. The document records `rootFormat`, optional root-descriptor content hash and length evidence, and every live artifact's path, logical change fingerprint, actual-byte xxHash128 hash, optional artifact length, and optional projection provenance. Provenance contains a root-relative logical path, format, positive format version, projection id, and artifact role so packages and sidecars can be grouped without inference. Ordinary files do not repeat their source length or modification time outside the readable fingerprint. An optional `artifactLength` is recorded only when the handler cannot supply the produced object's length, such as for a lazily built ZIP, so quick verification can still detect target truncation. The decompressed document is deterministic, ordered by live-relative path, human-readable, and protected by its own xxHash128 self-hash.

Readers retain explicit compatibility for schema versions 1 and 2. Version 1 has no root-format evidence and can be restored only when its representation is unambiguous. Version 2 adds `rootFormat` but predates descriptor evidence and provenance. A successful current backup rewrites either older logical document as version 3.

For ordinary files, the versioned quick fingerprint stores the known length and exact modification time converted to UTC directly as `stat-v1:<UTC timestamp>:<length>`. This is compact, canonical, and human-readable, but it is not proof of file contents. A same-length edit whose modification time is preserved, or same-length target corruption, can pass a quick check. YABT uses xxHash128 for actual-byte content hashes and aggregate ZIP identities. It is chosen for fast non-adversarial change detection; use byte-for-byte mode when complete content comparison is required.

Normal `backup` and `verify` use matching fingerprints plus the prior live change manifest to avoid opening unchanged source and target objects. A missing fingerprint, missing reliable target length, missing manifest, or changed fingerprint falls back to stream comparison. `backup --byte-for-byte` and `verify --byte-for-byte` bypass the quick match and compare complete streams. The byte-for-byte mode is the integrity check; the default verify result is explicitly a quick metadata check. `sync` remains a compatibility alias for `backup`.

When a mutating backup must compare streams, it copies the source bytes it is already comparing into a private delete-on-close local replay file. If the comparison finds a change, YABT finishes that one source read and uploads the captured snapshot instead of reopening the source. This both avoids duplicate source I/O and prevents a file changed between comparison and upload from producing a different archived version. Objects are currently processed sequentially, so local temporary storage must hold at most the largest projected object being compared. Verify and dry-run operations do not create replay files because they never upload. ZIP follows the same one-read rule: when an entry lacks enough metadata or a provider hash, its bytes are hashed while that same read is written into the package. The initial ZIP builder remains memory-backed; an eagerly built fallback package retains one backing buffer for replay, so nested incomplete-metadata packages can temporarily retain multiple buffers until their projection graph is released. A future large-package implementation needs an explicitly owned, spillable content lifetime.

The configured compression controls only what a mutating backup writes. Readers always inspect both supported filenames. If both exist, they are trusted only when both validate and have the same logical self-hash; a corrupt or conflicting pair forces full comparison. Before discarding multiple untrusted representations, backup creates `.yabt-change-manifest.invalid`. Its presence prevents a partially completed cleanup from leaving one stale manifest that appears trustworthy. The marker remains until the replacement manifest is safely written, then is deleted last. A successful mutating backup leaves exactly the configured representation live. Obsolete root change manifests and the root invalidation marker are deleted, never historized.

Target-native modification times are not compared with source times because uploads and remote servers assign different target times. The manifest preserves the source-derived fingerprint instead. Missing source timestamps do not receive a fake quick fingerprint; formats may use a separate deterministic timestamp only where an encoding such as ZIP requires one.

The old manifest is deleted before the first live mutation and the replacement is written last. An interrupted run therefore leaves no trusted live manifest and the next backup performs full comparisons. A mutating backup can discard and rebuild an invalid manifest; byte-for-byte mode can proceed without trusting it. Current MVP behavior does not search history for manifest metadata left there by older builds.

The current manifest is root-wide. It removes repeated byte scanning but still requires metadata enumeration and rewrites the JSON document when it changes. Folder-local or sharded manifests, filesystem event monitoring, Synology btrfs snapshot diffs, and other scalable delta sources remain future improvements behind change-detection abstractions.

## Restore Symmetry

Restore uses the same durable metadata that backup creates. The live change manifest records the root format and projection groups, package manifests describe each reversible ZIP representation, and the archived root descriptor supplies the authoritative archive identity and layout. A current-live restore pre-stages and hash-validates every file that it needs to write before the first destination mutation. It leaves identical files alone, moves changed or extra destination representations to destination history, and then installs the staged content with exact mirror or package-manifest timestamps.

After a successful restore, the filesystem root contains a separate plain `.yabt-logical-state-manifest.json`. Its self-hashed entries bind each logical file path's current canonical `stat-v1` fingerprint to the desired actual-byte content hash. On a later normal restore, a matching stat fingerprint and desired hash allow YABT to leave that file untouched without opening it. Missing, invalid, conflicting, or invalidated evidence falls back to hashing the destination file. `restore --byte-for-byte` bypasses this shortcut and validates all desired archive outputs before reconciliation. Restore uses `.yabt-logical-state-manifest.invalid` as a transaction marker, publishes rebuilt evidence after the live filesystem is correct, and removes the marker last.

The exact archived `.yabt-root.json` is installed when absent. An exact destination copy is left untouched. A different copy with the same archive id and layout requires `--replace-root-descriptor` and is historized before replacement; an incompatible archive id or layout is refused. YABT never rewrites machine-specific paths automatically, so the user edits obsolete filesystem locations after restore. Any destination live change manifest is invalidated and removed when restore changes live state or root metadata because it would otherwise describe the prior archive projection. Restoring a specific logical history version remains future work.

Restore must not require a cache or database. JSON metadata, manifests, package artifacts, and object-store paths remain sufficient.
