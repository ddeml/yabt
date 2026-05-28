# Architecture

YABT separates durable archive truth from runtime convenience.

The durable state is:

- Root archive metadata such as `.backup-root.json`.
- Per-folder metadata such as `.backup-policy.json`.
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

Initial object store providers:

- `fileSystem`
- `azureBlob`
- `webDav`

Operation direction determines whether a store is acting as the source, target, backup location, restore location, or reconciliation peer. The durable archive format should not depend on Azure-specific features.

## Format Providers

Folder representation is selected by a string `format` value in folder metadata. The format value is owned by the provider that implements it.

Initial format providers:

- `mirror`
- `zip`

`mirror` stores files individually. `zip` stores a logical folder as a package artifact plus adjacent metadata. Future providers such as `7z` or `tar.gz` may be added without changing `Yabt.Core`.

Each format provider owns its backup, restore, and verification behavior. Backup and restore remain conceptually symmetrical, but the operation direction matters. For example, the `zip` provider packs during backup and unpacks during restore.

## Project Boundaries

`Yabt.Core` contains durable archive concepts and cross-platform abstractions. It should not contain Azure, WebDAV, CLI, or provider-specific format logic.

`Yabt.Metadata` handles JSON formats such as `.backup-root.json`, `.backup-policy.json`, and package manifests.

Format provider projects implement folder representation behavior such as `mirror` and `zip`.

Object-store provider projects adapt storage systems such as the filesystem, Azure Blob Storage, and WebDAV.

`Yabt.Sync` coordinates root metadata loading, policy evaluation, provider resolution, backup, restore, verification, and reconciliation. The format provider owns the actual representation-specific work.

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

Restore should use the same durable metadata that backup creates. A folder restored from `/live` should recover the current hierarchy. A restore from `/hist` should recover a specific historical artifact or package version.

Restore must not require a cache or database. JSON metadata, manifests, package artifacts, and object-store paths remain sufficient.
