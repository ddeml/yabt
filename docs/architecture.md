# Architecture

YABT separates durable archive truth from runtime convenience.

The durable state is:

- Root archive metadata such as `.yabt-root.json`.
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

The object-store abstraction provides raw access to the underlying store. It should expose ordinary object operations such as listing, reading, writing, copying, moving, and deleting objects where the provider supports them. It must not know what `live` or `hist` mean, and it must not decide archive historization behavior.

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

The synchronizer owns historization. Before replacing or removing logical live objects, it should preserve the old representation under the configured history prefix. The exact historical sublayout may evolve, but it should remain inspectable.

## Archive Format Projectors

Folder representation is selected by a string `format` value in folder metadata. The format value is owned by the provider that implements it.

Initial format providers:

- `mirror`
- `zip`

`mirror` stores files individually. `zip` stores a logical folder as a package artifact plus adjacent metadata. Future providers such as `7z` or `tar.gz` may be added without changing `Yabt.Core`.

An archive format provider should act as a projector from a source folder and policy to an intended archive representation. It should not match source and target objects by itself, and it should not decide historization. The preferred future contract name is `IArchiveFormatProjector`.

The `mirror` projector maps source files one-to-one. The `zip` projector maps a source folder to a package artifact and manifest. The synchronizer then compares the projected representation to the target layout and applies writes, replacements, deletes, and history moves.

## Project Boundaries

`Yabt.Core` contains durable archive concepts and cross-platform abstractions. It should not contain Azure, WebDAV, CLI, or provider-specific format logic.

`Yabt.Common` contains shared cross-cutting primitives that should not pull in provider dependencies.

`Yabt.Metadata` handles JSON formats such as `.yabt-root.json`, `.yabt-policy.json`, and package manifests.

Format provider projects implement source-to-archive projection behavior such as `mirror` and `zip`.

Object-store provider projects adapt storage systems such as the filesystem, Azure Blob Storage, and WebDAV.

`Yabt.Sync` coordinates root metadata loading, policy evaluation, format projector resolution, layout mapping, historization, backup, restore, verification, and reconciliation.

`Yabt.Cli` provides the command entry point.

## Change Detection

The sync engine is expected to support multiple future change sources:

- Full manifest reconciliation.
- Incremental filesystem event monitoring.
- Synology btrfs snapshot diffs.
- Hash-based package manifest comparison.
- Periodic repair scans.

These belong behind change-detection abstractions so scalable delta sources can be introduced without changing storage layout.

## Restore Symmetry

Restore should use the same durable metadata that backup creates. A folder restored from the logical live branch should recover the current hierarchy. A restore from the logical history branch should recover a specific historical artifact or package version.

Restore must not require a cache or database. JSON metadata, manifests, package artifacts, and object-store paths remain sufficient.
