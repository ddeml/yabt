# Architecture

YABT separates archive truth from operational convenience.

The durable state is:

- The source filesystem.
- Per-folder metadata files that move with folders.
- Human-readable manifests next to package artifacts.
- Standard archive files when packaging is enabled.
- Azure Blob objects laid out in a directly browsable hierarchy.

Disposable state is:

- SQLite cache entries.
- Last scan accelerators.
- Reconciliation hints.
- Filesystem event checkpoints.

Disposable state may improve performance, but it must always be rebuildable from durable state.

## Project Boundaries

`Yabt.Core` contains the archive model and storage/change-detection abstractions. It has no Azure, SQLite, CLI, or platform-specific dependencies.

`Yabt.Metadata` handles JSON formats such as `.backup-policy.json` and package manifests.

`Yabt.Packaging` defines archive package contracts and naming. Concrete 7z, tar.gz, and zip implementations will be added behind `IArchivePackageBuilder`.

`Yabt.AzureBlob` adapts `IArchiveObjectStore` to Azure Blob Storage. Azure is an initial target, not a repository format.

`Yabt.Sync` will coordinate scanning, policy evaluation, packaging, upload, restore, verification, and reconciliation. The current scaffold only defines the orchestration shell.

`Yabt.Cli` provides the command entry point.

## Change Detection

The sync engine is expected to support multiple future change sources:

- Full manifest reconciliation.
- Incremental filesystem event monitoring.
- Synology btrfs snapshot diffs.
- Hash-based package manifest comparison.
- Periodic repair scans.

These belong behind `IChangeDetector` so a scalable delta source can be introduced without changing storage layout.

## Restore Symmetry

Restore should use the same durable metadata that sync creates. A folder restored from `/live` should recover the current hierarchy. A restore from `/hist` should recover a specific historical artifact or package version.

The restore workflow must not require SQLite. Cache can help locate objects faster, but JSON manifests and blob paths remain sufficient.
