# YABT

YABT (Yet Another Backup Tool) is an object-store archival synchronization tool. It replicates ordinary folders into directly browsable archive targets without turning the archive into a proprietary backup repository.

The initial target runtime is .NET 10 on Windows. The architecture keeps platform-specific concerns behind interfaces so Linux support, NAS workflows, WebDAV targets, Azure Blob Storage, and alternate storage targets can be added without changing the archive format.

## Goals

- Mirror the original folder hierarchy into directly browsable object stores.
- Keep a configurable logical live branch for current state and a configurable history branch for preserved historical state.
- Prefer append-mostly behavior for replaced or deleted content.
- Reclaim repeated history content through explicit, self-describing JSON references without deduplicating the live branch.
- Store intent and metadata in human-readable JSON files.
- Use standard archive formats such as zip when packaging folders.
- Keep archive targets directly browsable with ordinary tools such as the filesystem, Azure Storage Explorer, or WebDAV clients.
- Make restore symmetrical with sync wherever possible.
- Avoid metadata caching initially. Any future cache must be disposable and never the source of truth.

## Non-goals

- No proprietary repository format.
- No hidden mandatory catalog database.
- No opaque block store for the logical live branch.
- No deduplication of the logical live branch.
- No initial metadata cache.

## Why This Differs From Traditional Backup Tools

Many backup systems optimize for compactness, snapshots, and application-controlled restore workflows. YABT optimizes for inspectability and long-term durability. The archive should still make sense if the original tool is gone: files remain visible, packages use standard formats, manifests are JSON, and folder policies travel with the data they describe.

The filesystem plus metadata files are the source of truth. Object stores such as a plain filesystem, Azure Blob Storage, or WebDAV are durable replica/archive targets. A cache may accelerate reconciliation later, but it must always be rebuildable from durable metadata and object-store contents.

## Repository Layout

- `src/Yabt.Core` contains durable domain concepts and cross-platform abstractions.
- `src/Yabt.Common` contains shared cross-cutting primitives such as the base exception type.
- `src/Yabt.Metadata` reads and writes human-readable JSON metadata.
- `src/Yabt.Packaging` defines package building contracts and naming rules.
- Format projector projects own representations such as `mirror` and `zip`.
- Object-store provider projects adapt stores such as the filesystem, Azure Blob Storage, and WebDAV.
- `src/Yabt.Sync` holds synchronization orchestration, change-manifest comparison, and history deduplication.
- `src/Yabt.Cli` exposes the command surface.
- `docs` contains architecture and format notes.
- `spec` contains draft JSON schemas.
- `examples` contains a sample root descriptor and folder policy files.

## CLI

`sync`, `verify`, and history-only `deduplicate` are implemented. The default sync and verify comparison uses the durable change manifest and metadata fingerprints to avoid reading unchanged file contents. Use `--byte-for-byte` when a full content comparison is required:

```console
yabt sync <source-root>
yabt sync <source-root> --byte-for-byte
yabt verify <source-root>
yabt verify <source-root> --byte-for-byte
yabt deduplicate [archive-root]
yabt deduplicate [archive-root] --dry-run
```

`deduplicate` is a separate history maintenance operation so synchronization does not pay the cost of scanning history. It always confirms candidate duplicates byte-for-byte before replacing a historical materialization with a self-describing JSON reference.

Change manifests are rebuildable metadata, not historical file versions. A successful sync deletes obsolete root change-manifest files and the root invalidation marker instead of moving them into history. If that sync changed history, it also deletes the stale history catalog and then its invalidation marker; the next `deduplicate` run rebuilds the catalog. An invalidation marker should remain only when a sync or deduplication transaction was interrupted. MVP builds do not search history for metadata left there by older builds.

Additional commands remain scaffolded while their semantics are designed:

```console
yabt restore
yabt scan
yabt pack
yabt reconcile
```
