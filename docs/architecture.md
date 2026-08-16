# Architecture

YABT separates durable archive truth from runtime convenience.

The durable state is:

- Root archive metadata such as `.yabt-root.json`.
- The root `.yabt-change-manifest.json` used for fast live-state comparison.
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

Do not introduce a metadata cache initially. If a cache is added later, it must remain disposable and rebuildable from durable metadata, manifests, and object-store contents.

## Object Stores

YABT treats backup, restore, and reconciliation locations as object stores. A plain filesystem, Azure Blob Storage, and WebDAV are all peers behind the same object-store abstraction.

The object-store abstraction provides raw access to the underlying store. It exposes ordinary operations such as reading, writing, copying, moving objects, moving complete folders or prefixes, and folder-local traversal. Filesystem and WebDAV map a complete folder move to their native folder or collection operation; Azure Blob maps it to all objects under the exact prefix. A complete folder move includes hidden marker objects and native empty descendants and must not merge with an existing destination. The abstraction must not know what `live` or `hist` mean, and it must not decide archive historization behavior.

Traversal is hierarchical: callers ask for the files and immediate child folders under a folder prefix. Filesystem and WebDAV providers can expose native folders directly. Providers without real folders, such as Azure Blob Storage, emulate folders from object-name prefixes.

Empty folders may be represented by the reserved `.yabt-empty` marker object when a provider or archive representation cannot otherwise preserve them. The marker is YABT folder plumbing rather than ordinary source data.

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

## Archive Format Projectors

Folder representation is selected by a string `format` value in folder metadata. The format value is owned by the provider that implements it.

Initial archive format projectors:

- `mirror`
- `zip`

`mirror` stores files individually. `zip` stores a logical folder as a package artifact plus adjacent metadata. Future providers such as `7z` or `tar.gz` may be added without changing `Yabt.Core`.

An archive format projector acts on a source folder and policy to produce an intended archive representation. It should not match source and target objects by itself, and it should not decide historization. The projection contract is `IArchiveFormatProjector`.

When a subfolder uses a packaging format such as `zip`, its projected package artifacts replace the source folder entry and are composed into the logical parent folder. A `mirror` projection continues to preserve the source folder hierarchy. Packaging the selected source root places its artifacts at the target live root.

Projectors stream projected objects from `ProjectAsync`. Formats that can emit objects incrementally, such as `mirror`, should do so. Formats that need complete folder knowledge, such as `zip`, may collect their source folder first and then yield the finished package object.

The `mirror` projector maps source files one-to-one. The current initial `zip` projector maps a source folder to a package artifact; the adjacent manifest and external descriptor remain planned parts of the durable format. Package artifact names use a deterministic, algorithm-tagged full xxHash128 logical-representation hash and exclude per-run creation time, so an unchanged projection retains the same live key. ZIP identity includes ordered relative paths, archived lengths and modification times, per-object change fingerprints, and output-affecting compression settings. The synchronizer then compares the projected representation to the target layout and applies writes, replacements, deletes, and history moves.

## Project Boundaries

`Yabt.Core` contains durable archive concepts and cross-platform abstractions. It should not contain Azure, WebDAV, CLI, or provider-specific format logic.

`Yabt.Common` contains shared cross-cutting primitives that should not pull in provider dependencies.

`Yabt.Metadata` handles JSON formats such as `.yabt-root.json`, `.yabt-policy.json`, the live change manifest, and package manifests.

Format projector projects implement source-to-archive projection behavior such as `mirror` and `zip`.

Object-store provider projects adapt storage systems such as the filesystem, Azure Blob Storage, and WebDAV.

`Yabt.Sync` coordinates root metadata loading, policy evaluation, format projector resolution, layout mapping, historization, backup, restore, verification, and reconciliation.

`Yabt.Cli` provides the command entry point.

## Change Detection

The archive root contains `.yabt-change-manifest.json`. Each live projected object has a logical change fingerprint and, after synchronization, an xxHash128 hash and length of the actual stored artifact. The document is deterministic, ordered by live-relative path, human-readable, and protected by its own xxHash128 self-hash. It is durable comparison evidence, not a disposable cache and not a replacement for the planned per-package manifests.

For ordinary files, the versioned quick fingerprint is an xxHash128 digest derived from the known length and exact modification time converted to UTC. Hashing this metadata makes the value compact and canonical; it does not turn the metadata into proof of file contents. A same-length edit whose modification time is preserved, or same-length target corruption, can pass a quick check. YABT uses xxHash128 for both its logical fingerprints and actual-byte content hashes, while keeping the two meanings distinct. It is chosen for fast non-adversarial change detection; use byte-for-byte mode when complete content comparison is required.

Normal `sync` and `verify` use matching fingerprints plus the prior manifest to avoid opening unchanged source and target objects. A missing fingerprint, missing reliable target length, missing manifest, or changed fingerprint falls back to stream comparison. `sync --byte-for-byte` and `verify --byte-for-byte` bypass the quick match and compare complete streams. The byte-for-byte mode is the integrity check; the default verify result is explicitly a quick metadata check.

Target-native modification times are not compared with source times because uploads and remote servers assign different target times. The manifest preserves the source-derived fingerprint instead. Missing source timestamps do not receive a fake quick fingerprint; formats may use a separate deterministic timestamp only where an encoding such as ZIP requires one.

The old manifest is moved aside before the first live mutation and the replacement is written last. An interrupted run therefore leaves no trusted live manifest and the next sync performs full comparisons. A mutating sync can quarantine and rebuild an invalid manifest; byte-for-byte mode can proceed without trusting it.

The current manifest is root-wide. It removes repeated byte scanning but still requires metadata enumeration and rewrites the JSON document when it changes. Folder-local or sharded manifests, filesystem event monitoring, Synology btrfs snapshot diffs, and other scalable delta sources remain future improvements behind change-detection abstractions.

## Restore Symmetry

Restore should use the same durable metadata that backup creates. A folder restored from the logical live branch should recover the current hierarchy. A restore from the logical history branch should recover a specific historical artifact or package version.

Restore must not require a cache or database. JSON metadata, manifests, package artifacts, and object-store paths remain sufficient.
