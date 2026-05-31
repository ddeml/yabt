# YABT

YABT (Yet Another Backup Tool) is an object-store archival synchronization tool scaffold. It is intended to replicate ordinary folders into directly browsable archive targets without turning the archive into a proprietary backup repository.

The initial target runtime is .NET 10 on Windows. The architecture keeps platform-specific concerns behind interfaces so Linux support, NAS workflows, WebDAV targets, Azure Blob Storage, and alternate storage targets can be added without changing the archive format.

## Goals

- Mirror the original folder hierarchy into directly browsable object stores.
- Keep a configurable logical live branch for current state and a configurable history branch for preserved historical state.
- Prefer append-mostly behavior for replaced or deleted content.
- Store intent and metadata in human-readable JSON files.
- Use standard archive formats such as zip when packaging folders.
- Keep archive targets directly browsable with ordinary tools such as the filesystem, Azure Storage Explorer, or WebDAV clients.
- Make restore symmetrical with sync wherever possible.
- Avoid metadata caching initially. Any future cache must be disposable and never the source of truth.

## Non-goals

- No proprietary repository format.
- No hidden mandatory catalog database.
- No opaque block store for the logical live branch.
- No initial deduplication implementation.
- No initial metadata cache.
- No full synchronization engine in this scaffold.

## Why This Differs From Traditional Backup Tools

Many backup systems optimize for compactness, snapshots, and application-controlled restore workflows. YABT optimizes for inspectability and long-term durability. The archive should still make sense if the original tool is gone: files remain visible, packages use standard formats, manifests are JSON, and folder policies travel with the data they describe.

The filesystem plus metadata files are the source of truth. Object stores such as a plain filesystem, Azure Blob Storage, or WebDAV are durable replica/archive targets. A cache may accelerate reconciliation later, but it must always be rebuildable from durable metadata and object-store contents.

## Repository Layout

- `src/Yabt.Core` contains durable domain concepts and cross-platform abstractions.
- `src/Yabt.Common` contains shared cross-cutting primitives such as the base exception type.
- `src/Yabt.Metadata` reads and writes human-readable JSON metadata.
- `src/Yabt.Packaging` defines package building contracts and naming rules.
- Format provider projects will own representations such as `mirror` and `zip`.
- Object-store provider projects will adapt stores such as the filesystem, Azure Blob Storage, and WebDAV.
- `src/Yabt.Sync` holds orchestration contracts.
- `src/Yabt.Cli` exposes the future command surface.
- `docs` contains architecture and format notes.
- `spec` contains draft JSON schemas.
- `examples` contains a sample root descriptor and folder policy files.

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
