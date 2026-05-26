# YABT

YABT is a filesystem-to-Azure-Blob archival synchronization tool scaffold. It is intended to replicate ordinary folders into directly browsable object storage without turning the archive into a proprietary backup repository.

The initial target runtime is .NET 10 on Windows. The architecture keeps platform-specific concerns behind interfaces so Linux support, NAS workflows, and alternate storage targets can be added without changing the archive format.

## Goals

- Mirror the original folder hierarchy into Azure Blob Storage.
- Keep `/live` as the current filesystem state and `/hist` as preserved historical state.
- Prefer append-mostly behavior for replaced or deleted content.
- Store intent and metadata in human-readable JSON files.
- Use standard archive formats such as 7z, tar.gz, and zip when packaging folders.
- Keep Azure Blob Storage directly browsable with Azure Storage Explorer.
- Make restore symmetrical with sync wherever possible.
- Treat SQLite as a disposable cache only, never as the source of truth.

## Non-goals

- No proprietary repository format.
- No hidden mandatory catalog database.
- No opaque block store for `/live`.
- No initial deduplication implementation.
- No full synchronization engine in this scaffold.

## Why This Differs From Traditional Backup Tools

Many backup systems optimize for compactness, snapshots, and application-controlled restore workflows. YABT optimizes for inspectability and long-term durability. The archive should still make sense if the original tool is gone: files remain visible, packages use standard formats, manifests are JSON, and folder policies travel with the data they describe.

The filesystem plus metadata files are the source of truth. Azure Blob Storage is the durable replica/archive target. SQLite may accelerate reconciliation later, but it must always be rebuildable from the filesystem and blob metadata.

## Repository Layout

- `src/Yabt.Core` contains durable domain concepts and cross-platform abstractions.
- `src/Yabt.Metadata` reads and writes human-readable JSON metadata.
- `src/Yabt.Packaging` defines package building contracts and naming rules.
- `src/Yabt.AzureBlob` adapts the archive object-store abstraction to Azure Blob Storage.
- `src/Yabt.Sync` holds orchestration contracts and disposable cache scaffolding.
- `src/Yabt.Cli` exposes the future command surface.
- `docs` contains architecture and format notes.
- `spec` contains draft JSON schemas.
- `examples` contains sample folder policy files.

## CLI Skeleton

Future commands are scaffolded:

```console
yabt sync
yabt restore
yabt scan
yabt verify
yabt pack
yabt reconcile
```

These commands currently report that implementation is pending.
