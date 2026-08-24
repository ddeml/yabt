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
- Make restore symmetrical with backup wherever possible.
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
- Format projects provide bidirectional handlers for representations such as `mirror` and `zip`.
- Object-store provider projects adapt stores such as the filesystem, Azure Blob Storage, and WebDAV.
- `src/Yabt.Sync` holds synchronization orchestration, change-manifest comparison, and history deduplication.
- `src/Yabt.Cli` exposes the command surface.
- `docs` contains architecture and format notes.
- `spec` contains draft JSON schemas.
- `examples` contains a sample root descriptor and folder policy files.

## CLI

`backup`, live `restore`, `verify`, and history-only `deduplicate` are implemented. The default backup and verify comparison uses the durable change manifest and metadata fingerprints to avoid reading unchanged file contents. Use `--byte-for-byte` when a full content comparison is required:

```console
yabt backup <source-root>
yabt backup <source-root> --byte-for-byte
yabt verify <source-root>
yabt verify <source-root> --byte-for-byte
yabt restore <source-root> --destination-root <filesystem-folder>
yabt restore <source-root> --destination-root <filesystem-folder> --dry-run
yabt restore <source-root> --destination-root <filesystem-folder> --byte-for-byte
yabt restore <source-root> --destination-root <filesystem-folder> --replace-root-descriptor
yabt deduplicate [archive-root]
yabt deduplicate [archive-root] --dry-run
```

`sync` remains available as a compatibility alias for `backup`, but new commands and documentation should use `backup`.

If an Azure archive was created by an earlier development build, run one current `backup` before the first restore. This upgrades a schema-version-1 or schema-version-2 live change manifest to version 3 with exact root-descriptor evidence and projection provenance. Unchanged ordinary archive files are not re-uploaded; ZIP projections gain their adjacent and embedded manifests as part of the current durable representation.

`deduplicate` is a separate history maintenance operation so synchronization does not pay the cost of scanning history. It always confirms candidate duplicates byte-for-byte before replacing a historical materialization with a self-describing JSON reference.

Change manifests are rebuildable metadata, not historical file versions. A successful backup deletes obsolete root change-manifest files and the root invalidation marker instead of moving them into history. If that backup changed history, it also deletes the stale history catalog and then its invalidation marker; the next `deduplicate` run rebuilds the catalog. An invalidation marker should remain only when a backup or deduplication transaction was interrupted. MVP builds do not search history for metadata left there by older builds.

Restore reads the current logical live branch from the selected archive store. The `source-root` must be the folder containing `.yabt-root.json`; use `--target-store-id` when that descriptor contains multiple stores. Restore validates the live change manifest, reverses nested ZIP projections from their durable provenance, and reconciles the filesystem destination to that live state. Each ZIP has a visible adjacent JSON manifest and an identical embedded manifest, so ordinary hash-looking ZIP files are never guessed to be packages. Exact source modification times come from the package manifest rather than the two-second ZIP timestamp field.

Identical files remain untouched. A successful restore writes `.yabt-logical-state-manifest.json` at the destination root so a later normal restore can recognize an unchanged file from its current `stat-v1` metadata plus the previously validated content hash. Missing, invalid, or invalidated evidence falls back to hashing the file; `restore --byte-for-byte` bypasses this shortcut, validates every desired archive output, and reads each corresponding existing destination file. Before changing anything, YABT stages and hash-validates every file that must be written, so temporary disk space up to the total size of new and changed files may be required. Replaced files, extra files, extra folders, and file/folder shape conflicts move to one new timestamped version under the destination's configured history prefix before staged content is installed.

Backup stores the validated source `.yabt-root.json` in the archive byte-for-byte, outside packages, and the version-3 change manifest binds its hash and length. Restore installs those exact bytes in a new destination, including any machine-specific filesystem paths. YABT deliberately does not rewrite stale paths; edit them manually after restore if the new machine needs different locations. An existing byte-identical descriptor is left alone. A different descriptor with the same archive id and layout requires `--replace-root-descriptor` and is moved to history before replacement. A different archive id or layout is always refused. Any stale destination live change manifest is safely invalidated and removed. Restore does not yet select historical archive versions.

Additional commands remain scaffolded while their semantics are designed:

```console
yabt scan
yabt pack
yabt reconcile
```
