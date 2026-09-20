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

`backup`, live `restore`, archive `verify`, restore-path `verify-restore`, and history-only `deduplicate` are implemented. The default backup and archive verify comparison uses the durable change manifest and metadata fingerprints to avoid reading unchanged file contents. Use `--byte-for-byte` when a full archive content comparison is required:

```console
yabt backup <source-root>
yabt backup <source-root> --byte-for-byte
yabt verify <source-root>
yabt verify <source-root> --byte-for-byte
yabt restore <source-root> --destination-root <filesystem-folder>
yabt restore <source-root> --destination-root <filesystem-folder> --dry-run
yabt restore <source-root> --destination-root <filesystem-folder> --byte-for-byte
yabt restore <source-root> --destination-root <filesystem-folder> --replace-root-descriptor
yabt verify-restore <source-root> --destination-root <filesystem-folder>
yabt deduplicate [archive-root]
yabt deduplicate [archive-root] --dry-run
```

`sync` remains available as a compatibility alias for `backup`, but new commands and documentation should use `backup`.

`verify-restore` compares relative path spelling and item kind across the complete source and restored filesystem trees, including empty directories, and always reads matching files byte-for-byte. Filesystem timestamps and attributes are not compared because they are not durable for every restored object. The root `.yabt-logical-state-manifest.json` and the root `.yabt-tmp` tree are ignored because they are restore plumbing; same-named nested items are compared normally. All other root paths, including `.yabt-root.json` and a leftover logical-state invalidation marker, remain part of the comparison. Independent file-access failures do not stop the traversal: the final nonzero result lists every uncompared path, both full file paths involved, and the underlying operating-system reason.

Add `--log-file` to any command to write a uniquely named diagnostic log in the platform's per-user log directory as well as logging to the console. The default directory is `%LOCALAPPDATA%\Yabt\Logs` on Windows, `$XDG_STATE_HOME/yabt/logs` (falling back to `~/.local/state/yabt/logs`) on Linux, and `~/Library/Logs/Yabt` on macOS. Use the equals form `--log-file=<path>` to choose the file explicitly; relative paths use the current working directory, and YABT refuses to overwrite or append to an existing file:

```console
yabt backup <source-root> --log-file
yabt backup <source-root> --log-file=D:\Logs\nightly-backup.log
```

The console defaults to information-level messages. An enabled file log additionally captures debug messages such as reads, comparisons, unchanged files, and YABT metadata operations. YABT creates one new file per command invocation and does not currently delete old log files automatically. It rejects a log path that overlaps the command's source, selected filesystem archive, or restore destination so the growing log cannot become synchronization input or block creation of a root.

If an Azure archive was created by an earlier development build, run one current `backup` before the first restore. This upgrades a schema-version-1 or schema-version-2 live change manifest to version 3 with exact root-descriptor evidence and projection provenance. Unchanged ordinary archive files are not re-uploaded; ZIP projections gain their adjacent and embedded manifests as part of the current durable representation.

`deduplicate` is a separate history maintenance operation so synchronization does not pay the cost of scanning history. It always confirms candidate duplicates byte-for-byte before replacing a historical materialization with a self-describing JSON reference.

Change manifests are rebuildable metadata, not historical file versions. A successful backup deletes obsolete root change-manifest files and the root invalidation marker instead of moving them into history. If that backup changed history, it also deletes the stale history catalog and then its invalidation marker; the next `deduplicate` run rebuilds the catalog. An invalidation marker should remain only when a backup or deduplication transaction was interrupted. MVP builds do not search history for metadata left there by older builds.

Restore reads the current logical live branch from the selected archive store. The `source-root` must be the folder containing `.yabt-root.json`; use `--target-store-id` when that descriptor contains multiple stores. Restore validates the live change manifest, reverses nested ZIP projections from their durable provenance, and reconciles the filesystem destination to that live state. Each ZIP has a visible adjacent JSON manifest and an identical embedded manifest, so ordinary hash-looking ZIP files are never guessed to be packages. Exact source modification times come from the package manifest rather than the two-second ZIP timestamp field.

Identical files remain untouched. A successful restore writes `.yabt-logical-state-manifest.json` at the destination root so a later normal restore can recognize an unchanged file from its current `stat-v1` metadata plus the previously validated content hash. Missing, invalid, or invalidated evidence falls back to hashing the file; `restore --byte-for-byte` bypasses this shortcut, validates every desired archive output, and reads each corresponding existing destination file. Restore processes files through a bounded pipeline whose `Sync:RestoreMaximumConcurrency` setting defaults to `5`. Each new or changed file is downloaded once into a unique file under the destination's `.yabt-tmp`, length- and hash-validated during that write, historized only after validation, and atomically moved into place without a second local copy. Independent archive or destination read failures are collected and reported together at the end while readable files can remain as rerun checkpoints. On such an incomplete run, failed live files and unrelated extras remain untouched and final state manifests are not published. Temporary final-output storage is therefore bounded by the in-flight files rather than the complete restore. Unrelated extra files and folders move to one new timestamped history version only after every desired write succeeds.

Backup applies the same rule to unreadable source files, including files inside ZIP projections. It reports every source path and underlying reason, continues with independently readable objects, leaves failed replacements and unrelated target extras untouched, and does not publish a trusted live change manifest for the incomplete run. Mutation, history, lock, staging, and provider-write failures still stop immediately because continuing after those failures would not be safe.

Backup stores the validated source `.yabt-root.json` in the archive byte-for-byte, outside packages, and the version-3 change manifest binds its hash and length. Restore installs those exact bytes in a new destination, including any machine-specific filesystem paths. YABT deliberately does not rewrite stale paths; edit them manually after restore if the new machine needs different locations. An existing byte-identical descriptor is left alone. A different descriptor with the same archive id and layout requires `--replace-root-descriptor` and is moved to history before replacement. A different archive id or layout is always refused. Any stale destination live change manifest is safely invalidated and removed. Restore does not yet select historical archive versions.

Additional commands remain scaffolded while their semantics are designed:

```console
yabt scan
yabt pack
yabt reconcile
```
