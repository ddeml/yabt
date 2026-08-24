# Agent Notes

This file records durable project guidance for Codex and other AI agents working in this repository. Treat it as standing context unless the user explicitly updates it.

## Project Goal

YABT is a .NET 10 object-store archival synchronization tool.

It should replicate ordinary folders into archive targets such as a plain filesystem, Azure Blob Storage, or WebDAV in a way that remains directly inspectable, understandable, and restorable without proprietary tooling. The project runs primarily on Windows at first, but architecture should remain portable and Linux-compatible where practical.

The system is a long-term durable archival replication tool, not a proprietary backup repository format.

## User Context

Richard is the main user and human maintainer. He is a seasoned developer with 30+ years of experience, especially in C#/.NET, MS-SQL, Oracle, and related ecosystems.

Leo is a new contributor to the project and is relatively new to software development. When Leo is the active user, use beginner-friendly language, explain project-specific terminology and implementation choices, and provide enough context to make the next step understandable. Keep the tone respectful and practical; do not assume deep .NET or C# experience, but also avoid condescension.

When Richard is the active user, do not over-explain basic programming concepts. Prefer concise engineering tradeoffs, explicit assumptions, and clear implementation choices. When introducing AI-agent workflow suggestions, keep them practical and lightweight.

## Architectural Principles

- Archive targets must remain directly browsable with ordinary tools, such as the filesystem, Azure Storage Explorer, or WebDAV clients.
- Backup and restore should be symmetrical where practical: source and target locations are both object stores, and the operation direction determines backup, restore, verification, or reconciliation behavior.
- Archive object layout should mirror the original folder hierarchy.
- The archive must remain understandable and restorable without proprietary tooling.
- Standard archive formats should be used when packaging is enabled.
- Metadata should be stored in human-readable JSON files.
- Do not implement metadata caching initially. If a cache is added later, it may be used only as a disposable performance optimization.
- Any future cache must never be the authoritative source of truth.
- The filesystem plus metadata files are the source of truth.
- Object stores are durable replica/archive targets.
- Prefer append-mostly behavior.
- Do not store secrets in durable archive metadata. Use runtime configuration, OS credential stores, managed identity, environment-provided credentials, or external secret stores.

## Live And Hist

The archive layout conceptually uses logical live and history branches. Physical prefixes are configured in `.yabt-root.json`:

```text
livePrefix = ""
histPrefix = ".yabt-hist"
```

The default `livePrefix` is empty, so an ordinary source folder can be the logical live branch without moving its data under a `live` child folder.

The default `histPrefix` is `.yabt-hist`. Deleted or replaced content should generally move to the configured history prefix instead of being deleted.

When an entire logical live folder becomes obsolete, such as when its format changes from `mirror` to `zip`, move its complete folder or prefix representation to history as one operation at the synchronization layer. Preserve hidden `.yabt-empty` markers and native empty descendants; do not recursively delete the folder after moving only its visible objects. Providers without native folders should apply the equivalent operation to every object under the exact prefix.

Occurrences historized by one mutating command share one fresh timestamp-root folder or prefix. If that timestamp root already exists, allocate the next numeric suffix, such as `20260821T120000Z-1`; never merge a later command's occurrences into an existing timestamp root. If a provider can contain an exact object and same-named descendant prefix simultaneously, place those incompatible representations in separate fresh suffixed roots so neither blocks the other.

Archive-style roots may still configure explicit prefixes such as `livePrefix = "live"` and `histPrefix = "hist"` when that layout is preferable.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root and are not ordinary live data.

YABT reserves `.yabt-tmp` at the archive root for provider/runtime plumbing. The filesystem provider uses it for same-filesystem upload staging, and archive-mutating commands use `.yabt-tmp/archive-mutation-lock.json` to coordinate access. Filesystem conditional mutations also serialize through an empty `conditional-mutation.lock`, which may remain while no command is active. Each filesystem upload should use a uniquely named temporary file, remove that file after the completed file is atomically moved into place or after a controlled failure, and leave the shared staging directory in place. Deleting and recreating the shared directory can race concurrent YABT processes. Other nonempty contents may belong to an active upload or an interrupted run. Reject live or history prefixes that overlap `.yabt-tmp`, including case variants, so plumbing can never mix with archive data.

Do not deduplicate the logical live branch.

History deduplication is an explicit maintenance operation performed by the `deduplicate` command. `backup` must not scan or compare history for duplicates. The command accepts an archive root, optional target store id, and optional dry-run flag; byte-for-byte confirmation is mandatory and is not an optional comparison mode.

The history branch has its own durable `.yabt-history-manifest.json`, separate from the live change manifest. It records every historical occurrence and whether that occurrence is materialized or represented by a deduplication reference. The history manifest is a rebuildable index and must never contain exclusive information. Materialized entries contain only information that can be recovered by scanning or opening their actual objects, while each reference repeats its complete occurrence entry.

Deduplication may replace only historical objects. Leave one stable materialized historical copy for every deduplicated content hash. The current command scans only history and does not use or modify matching live objects; a future resolver may treat live objects as optional extra candidates, but never as the sole backing copy for historical references. References identify content by hash rather than by another reference path, so reference chains are impossible and a backing object's path can change without rewriting every reference.

Name a reference by appending `.yabt-ref.json` to the complete original filename, including its extension, for example `report.pdf.yabt-ref.json`. The reference must be human-readable, versioned JSON with document type `yabt.historyContentReference`, include the exact complete occurrence entry recorded in the history manifest, and protect that entry with a self-hash. History entries record the logical relative path, stored relative path, representation, content hash, content length, and optional observable last-modified time, content type, and provider metadata. Do not put a source-only change fingerprint or other unrebuildable information in the history manifest.

If that stored reference name is already occupied, append a deterministic numeric sequence before `.yabt-ref.json`, for example `report.pdf.1.yabt-ref.json`. Never overwrite an existing historical object to obtain the preferred reference name.

Treat an equal length and xxHash128 value as a duplicate candidate only. Compare complete byte streams before removing a redundant materialization. Establish and verify the stable materialized copy and durably write its reference metadata before deleting the redundant bytes. An interrupted command may leave an unnecessary full copy but must never leave a reference without a materialized backing object. Deduplication and other archive-mutating commands must not mutate the same archive concurrently.

The history catalog uses `.yabt-history-manifest.invalid` only while its current contents must not be trusted. When a successful `backup` adds or changes history, it deletes the now-stale `.yabt-history-manifest.json` and then deletes the invalidation marker before completing. A successful `deduplicate` removes the marker only after references, the replacement catalog, and guarded deletions complete. The marker may remain after an interrupted `backup` or `deduplicate`, but it must not remain after either command succeeds. Do not add legacy cleanup that searches history for manifests or markers historized by earlier MVP builds unless Leo explicitly requests it. YABT's archive lock coordinates YABT writers; users and other programs must not modify the archive while an archive-mutating command is active.

The optional root setting `historyDeduplicationTinyFileMaximumBytes` defaults to 4096. Objects at or below the effective threshold are not eligible for deduplication. Also retain a materialized object when its reference and index overhead would not produce a net storage saving. Exclude YABT metadata, `.yabt-empty`, reference files, manifests, and provider plumbing from deduplication.

## Archive Root Metadata

The archive root should contain a human-readable descriptor named:

```text
.yabt-root.json
```

This file identifies the archive, records layout information, and describes known object stores by provider-owned string names.

Treat the validated descriptor document as reserved root metadata, outside the logical live branch and outside every package. A mutating `backup` copies the source descriptor's exact bytes to the archive, including whitespace, property order, and any UTF-8 byte-order mark. If an existing archived descriptor has the same archive id and layout but different bytes, move the old document to history before replacing it. Refuse an archived descriptor with a different archive id or layout. Schema-version-3 live change manifests bind the carried descriptor with its actual-byte xxHash128 value and length.

Restore the archived descriptor bytes unchanged. Do not rewrite `rootRole`, store declarations, or filesystem paths for the destination machine: the user must manually reconfigure any paths that are no longer valid. Install the descriptor when it is absent and leave an exact match untouched. If a destination descriptor has the same archive id and layout but different bytes, require `restore --replace-root-descriptor`, historize the old document, and then install the archived bytes. Refuse a destination descriptor with a different archive id or layout even when that option is present.

It may include an optional `rootRole` value such as `source` or `target` to indicate the intended default role of the root. The role is advisory and does not imply a physical layout; command direction still determines backup, restore, verification, or reconciliation behavior.

It may include an optional `defaultStoreId` value to select the default object store when a command does not explicitly specify one. If neither command input nor root metadata selects a store, use the first configured store and warn when multiple stores are available.

It may contain non-secret provider details such as filesystem paths, WebDAV endpoints, and credential references supported by those providers. It must not contain account keys, SAS tokens, passwords, client secrets, or other credentials.

An Azure Blob store declaration contains only its `id`, `kind`, and an optional `configSectionPath`. The path defaults to `ObjectStores:AzureBlob`. Resolve the service URI, container, prefix, and authentication settings from that section of the application's merged runtime configuration. A nonempty `ConnectionString` takes precedence. Otherwise, require `ServiceUri` and authenticate with the registered Azure `TokenCredential`, which defaults to `DefaultAzureCredential`; hosts may register a more specific credential such as `ManagedIdentityCredential`. A token credential does not identify the storage account endpoint. Do not put Azure endpoint, container, prefix, `credentialRef`, or authentication values directly in `.yabt-root.json`, and require an absolute HTTPS service URI without user information, a query, or a fragment so credentials cannot hide in the endpoint.

Object store roles are operation-specific. A store may be a source, target, backup location, restore location, or reconciliation peer depending on the command.

Initial object store providers:

- `fileSystem`
- `azureBlob`
- `webDav`

## Object Store Traversal

`IObjectStore` should expose folder-local traversal rather than recursive flat listing. The object store contract should answer "what files and immediate child folders are inside this folder prefix?" so backup can compare source and target incrementally and future traversal can parallelize child folders.

Providers that do not have real directories, such as Azure Blob Storage, should emulate immediate child folders from object key prefixes. Do not rely on global ordering of recursive object listings for backup correctness.

The filesystem provider must not traverse directory reparse points or symbolic-link directories below an object-store root. Following one during destructive history maintenance could escape the configured archive root. A linked directory that is intentionally an archive root should be configured as the root itself instead.

Empty folders may be represented with the reserved marker file:

```text
.yabt-empty
```

The current `mirror` backup projection creates this marker for every empty folder on every target, including filesystem targets, because the projection contract emits objects rather than native folder-creation operations. The mirror restore projection reverses the marker into a native directory. The ZIP handler also stores a zero-byte marker entry for every native empty descendant and reverses it into a native directory so packaging and restore preserve those folders. The marker remains in the logical live branch while the folder is empty, moves to history when that folder gains content or changes format, and is hidden from normal YABT traversal. Treat it as durable YABT folder plumbing, not ordinary user data or temporary cleanup residue.

## Formats And Packaging

Folders may optionally be packaged before upload. Packaging is controlled by metadata files inside folders.

The primary policy file name is:

```text
.yabt-policy.json
```

The policy file should use one provider-owned string value named `format`. Do not model durable format names as C# enums.

Initial archive format handlers:

- `mirror`
- `zip`

Do not differentiate durable `ArchiveFormat` and `PackageMode` concepts. Different folder representations are archive formats. Do not implement an `auto` format initially.

Each archive format handler owns its format name and both transformations: backup projects a source folder plus policy into an intended archive representation, while restore receives the complete artifact group for one projection and reverses it into logical files, native directories, and possibly more provenanced artifacts. Format handlers validate their own portable representation, such as ZIP entry paths and linked entries. Cross-artifact path collisions, destination-specific validation, historization, target comparison, and delete handling belong to the archive synchronizer, not to the format handler.

A handler whose artifacts replace their source folder beside that folder must emit nonempty, complete projection provenance for every artifact. All artifacts for that logical folder share one logical path, format, format version, and projection id, and use distinct artifact roles. A successful current backup must never create a packaged-root manifest without one complete root projection group.

The bidirectional format contract is `IArchiveFormatHandler`. Its explicit `ProjectBackupAsync` and `ProjectRestoreAsync` methods share `ArchiveProjectedObject` as the logical desired-file representation. A restore projection may own disposable staging for as long as its projected objects can be opened. Do not keep a central format registry in `Yabt.Core`.

Package artifacts should be immutable and use deterministic content- or manifest-addressed names:

```text
<folder-name>.<hash-algorithm>-<full-manifest-or-content-hash>.<extension>
```

Example:

```text
Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.zip
```

Do not put the projection or package creation time in the package artifact name. Creation time belongs in the manifest, while synchronization history paths record when a live artifact was replaced. Any creation time embedded inside a package must be stable for that content version rather than regenerated on every projection, so it does not make the package vary on every run. YABT-owned JSON hash values use the full 128-bit xxHash128 value encoded as canonical unpadded Base64URL. Package file names encode the same full value as lowercase unpadded Base32hex so their identity remains stable on case-insensitive file systems. The current ZIP handler hashes a logical representation containing ordered source paths, archived lengths and modification times, versioned per-object change fingerprints, and output-affecting compression settings. A normal file fingerprint stores its known length and exact UTC modification time directly in a canonical human-readable string; incomplete metadata falls back to a provider content hash or a byte scan. Do not truncate the aggregate hash to an arbitrary short prefix. An unchanged logical source manifest should produce the same live object key, while changed logical source manifests produce new immutable keys. Older package versions should remain preserved in history. Provider-supplied checksums may use provider-owned algorithms and must not be treated as comparable unless their algorithm is known.

When a subfolder is packaged, place its package artifact and adjacent metadata directly in the logical parent folder. Do not create a target folder corresponding to the packaged source folder. Packaging the selected root still places its artifacts at the target live root.

The adjacent package manifest is the visible archive-side folder metadata. Place it in the same parent target folder as the package artifact. Keep the source-side policy inside the source folder so it moves naturally with that folder.

The exact `.yabt-policy.json` source object travels through its folder representation and reappears on restore. For a ZIP folder it is stored as a normal ZIP payload entry, while the adjacent package manifest also contains the parsed canonical policy snapshot needed to understand the artifact without opening it.

## Manifests

Each ZIP projection has:

- A package named `<package-name>.zip`.
- An adjacent manifest named by appending `.yabt-manifest.json` to the complete package filename.
- An embedded manifest at the reserved ZIP entry `.yabt-package-manifest.json`.

The adjacent and embedded manifest bytes must be identical. The manifest is versioned, self-hashed JSON and includes the root-relative logical source path, deterministic creation time, format and format version, projection id, package filename, canonical policy snapshot, total bytes, and ordered entries. Every entry records its kind, logical relative path, stored ZIP path, exact UTC modification time, length, and actual-byte content hash. Entries that contain another format projection also carry that projection's logical path, format, format version, projection id, and artifact role.

Package manifests must not contain the finished package's content hash because embedding that value would create a hash cycle. The root live change manifest binds the actual package and adjacent-manifest bytes instead. Restore first validates the adjacent manifest and uses its entry evidence to plan lazily. Before any needed payload is written, it validates the complete package plus the identical embedded manifest and validates every staged output against its manifest hash. Projection provenance lets the synchronizer group package artifacts and recursively reverse nested format projections without guessing from file extensions.

Artifacts with the same projection provenance are one consistency unit. When any member must be created or replaced, backup must revalidate every sibling that matched only fast evidence against the same handler materialization before publishing the live change manifest. A successful backup must never leave a package from one materialization beside a manifest from another. A ZIP projection containing no file payload must still validate its complete package and byte-identical embedded manifest before restore may mutate the destination; a current YABT ZIP manifest cannot have zero entries.

The current ZIP package-manifest safety limit is 16 MiB. Enforce the same bound while building, reading the adjacent copy, and reading the embedded copy so backup cannot publish an artifact that restore will categorically refuse.

ZIP metadata timestamps use the deterministic exact source timestamp when it is representable. Clamp only the ZIP header value to the 1980 through 2107 DOS range when necessary; keep the exact UTC timestamp in the adjacent and embedded manifest and restore from that manifest value.

Manifest JSON is human-readable, canonically ordered, and deterministic.

## Metadata

Per-folder metadata files define intent and configuration, not operational state.

Metadata files should move with folders and survive reorganization.

Operational state belongs in disposable cache only.

The root change manifest is durable, human-readable comparison evidence rather than operational state or a disposable cache. Its logical content is JSON and it may be stored as `.yabt-change-manifest.json.br` using standard Brotli compression or as plain `.yabt-change-manifest.json`. Schema version 3 records the provider-owned root format, optional exact root-descriptor content hash and length evidence, and each live-relative artifact's fingerprint, actual-byte content hash, optional artifact length, and optional projection provenance. Provenance groups the artifacts of one format projection and records their root-relative logical path, provider-owned format, positive format version, projection id, and artifact role. Do not duplicate a normal file's length or modification time outside its readable `stat-v1` fingerprint. Record a separate optional artifact length only when the format handler cannot report the produced object's length, such as for a lazily built ZIP; this preserves quick target-truncation detection. Do not use target-native modification time as a substitute for the persisted source-derived fingerprint.

The root change-manifest reader accepts schema versions 1 and 2 written by earlier development builds. Version 1 lacks root-format evidence; restore may use it only when the representation is unambiguous. Version 2 adds `rootFormat` but has no root-descriptor evidence or projection provenance. A successful current backup upgrades either version to schema version 3, whose entries all require actual-byte `contentHash` evidence. Fields introduced by a later schema are forbidden in earlier documents even when their JSON value is `null`. During MVP development, accept only these explicit compatibility versions and the exact current metadata filenames and hash formats; do not add other legacy aliases or migrations unless Leo explicitly requests them.

After restore, the filesystem destination has a separate plain `.yabt-logical-state-manifest.json` with document type `yabt.logicalStateManifest`. Each canonical entry records `logicalRelativePath`, the destination's current canonical `stat-v1` fingerprint, and the desired actual-byte `contentHash`. Its self-hash and `.yabt-logical-state-manifest.invalid` transaction marker make it safe only as quick, rebuildable evidence. It never replaces the restored files as the source of truth.

## Change Detection

Normal `backup` and `verify` use the root change manifest and versioned metadata fingerprints to avoid opening unchanged objects. A normal file fingerprint is the human-readable `stat-v1:<UTC timestamp>:<length>` string. Keep `ChangeFingerprint` distinct from `ContentHash`: the fingerprint records quick-change metadata while the xxHash128 content hash covers the actual projected artifact bytes. xxHash128 provides fast accidental-change detection with a very low probability of an accidental collision; it is not intended as a cryptographic or adversarial integrity guarantee.

Do not store literal file contents in change manifests, even when a file is shorter than its hash. Small files may contain passwords, tokens, or other secrets, and durable metadata must remain comparison evidence rather than backup content.

Metadata fingerprints are not content proof. Same-length data whose timestamp was preserved, and same-length target corruption, may pass a quick check. `backup --byte-for-byte`, `restore --byte-for-byte`, and `verify --byte-for-byte` must bypass the applicable fast match and read the complete relevant contents. Missing timestamps, incomplete target length metadata, absent or invalid manifest evidence, or changed fingerprints must fall back to content reads. Do not use the ZIP-safe 1980 timestamp fallback as a general change-detection timestamp.

A mutating backup that falls back to stream comparison must load each projected source object only once. Retain the exact compared materialization in private delete-on-close local staging and upload that snapshot if it differs; do not reopen the source after comparison. This staging is ephemeral operation state, not archive metadata or a durable cache, and it must not use the target provider's `.yabt-tmp` namespace. Processed objects are staged individually, so the current sequential synchronizer needs local temporary space for at most one compared projected object at a time. Verification and dry runs do not need a replay copy because they never upload. When ZIP input metadata is too incomplete to identify an entry without reading its bytes, calculate that entry fingerprint while copying the same read into the ZIP instead of scanning and reopening the source file. The current ZIP backup handler is memory-backed; an eagerly built fallback package retains one backing buffer so it can be replayed without rereading its source files. Do not add another full-package copy. A future large-package implementation should introduce an explicitly owned, spillable projected-content lifetime.

Store the live change manifest at the archive root, outside an explicit `livePrefix`, with logical live-relative entry paths. `changeManifestCompression` in `.yabt-root.json` accepts `brotli` or `none` and defaults to `brotli`. Writers leave exactly one configured live representation, but readers always inspect both `.yabt-change-manifest.json.br` and `.yabt-change-manifest.json`. When both exist, trust them only if both validate and have the same logical self-hash; otherwise require full comparison. Treat both names and `.yabt-change-manifest.invalid` as reserved internal metadata when `livePrefix` is empty. The invalidation marker protects recovery while untrusted representations are being discarded; readers must not trust manifest evidence while it exists. Delete every obsolete root change-manifest representation instead of moving it to history, write the replacement last, and delete the root invalidation marker last. A successful mutating backup must never historize a root change manifest or its invalidation marker. Do not add legacy cleanup that scans history for such metadata left by earlier MVP builds unless Leo explicitly requests it. A mutating backup should discard and rebuild invalid or conflicting manifests; byte-for-byte comparison must remain usable without trusting them.

Normal restore may trust a valid destination logical-state entry without opening the destination file only when the current filesystem `stat-v1` fingerprint matches and the desired content hash equals the recorded hash. Otherwise hash the destination file. `restore --byte-for-byte` bypasses this shortcut and validates every desired archive output before reconciliation. Regardless of the shortcut, pre-stage and hash-validate all new or changed outputs before the first destination mutation. When restore changes live state and prior logical-state evidence exists, create the invalidation marker before mutation; publish a rebuilt manifest after the filesystem is reconciled and delete the marker last. A leftover marker or invalid document disables quick evidence and causes content reads on the next restore.

The current root-wide manifest avoids repeated byte scans but still performs metadata traversal and O(total objects) manifest work. Architecture should allow future folder-local or sharded manifests, Synology btrfs snapshot diffing, filesystem event monitoring, and incremental reconciliation for millions of files. Any cache remains disposable and non-authoritative.

## Initial Technical Stack

- .NET 10
- C#
- Microsoft.Extensions.Hosting
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Logging
- System.Text.Json
- Azure.Storage.Blobs SDK
- WebDAV client support, to be selected when implementation starts

Prefer async APIs throughout.

Enable nullable reference types and analyzers.

## Repository Structure

Expected high-level folders:

- `src`
- `tests`
- `docs`
- `spec`
- `examples`

Expected source projects:

- `Yabt.Common`
- `Yabt.Core`
- `Yabt.AzureBlob`
- `Yabt.FileSystem`
- `Yabt.WebDav`
- `Yabt.Format.Mirror`
- `Yabt.Format.Zip`
- `Yabt.Packaging`
- `Yabt.Metadata`
- `Yabt.Sync`
- `Yabt.Cli`

Project boundaries may evolve, but keep storage adapters, domain models, metadata handling, packaging, sync orchestration, and CLI concerns separated.

## Testing

Use MSTest for unit tests.

Maintain a separate test project for each tested main project. Name test projects by appending `.Tests` to the tested project name, for example `Yabt.Format.Mirror.Tests` for `Yabt.Format.Mirror`.

A shared test assembly is acceptable only for shared test helpers or tests that address a corresponding shared production project directly.

Use `Yabt.Tests` for shared test helpers such as in-memory object stores and other test infrastructure.

## CLI

Command surface:

- `backup`
- `restore`
- `scan`
- `verify`
- `pack`
- `reconcile`
- `deduplicate`

`backup`, current-live `restore`, `verify`, and history-only `deduplicate` are implemented. `sync` remains a compatibility alias for `backup`; new code and documentation should use `backup`. Restore requires a valid live change manifest and reconciles a filesystem destination to the selected live archive state. Leave byte-identical files untouched. Pre-stage and hash-validate every file that will be written before the first destination mutation, using bounded open handles even when many files are staged. Before replacing changed files or removing extra destination files, folders, or file/folder shape conflicts from the live destination, move their complete representations to the destination's history prefix. Restore both mirror and ZIP file timestamps from durable exact metadata rather than provider upload or ZIP DOS timestamps. Recursively reverse nested format projections using manifest provenance and refuse path collisions or case-sensitive archive paths that cannot be represented distinctly on the destination filesystem. Restore the exact archived root descriptor according to the mismatch rules above, invalidate and remove any destination live change manifest when live state or root metadata changes, and rebuild the destination logical-state manifest transactionally. Restore does not yet select historical archive versions. Keep scan, pack, and reconcile behavior scaffolded until their semantics are designed.

`backup` and `restore` enter a common direction-selected archive operation boundary and then use direction-specific preparation and application pipelines. Both pipelines share format handlers, desired-object reconciliation, and historization where their safety requirements permit. Backup must retain its at-most-one prepared object streaming bound, while restore must retain full write preflight before its first mutation, so direction-specific action consumers are required inside the shared reconciliation pipeline.

## Coding Style

- Follow idiomatic modern C#.
- Prefer small, explicit domain records and interfaces.
- Keep Azure-specific types out of `Yabt.Core`.
- Keep any future cache concerns out of durable model code.
- Use async APIs for I/O and cloud operations.
- Use `System.TimeProvider` instead of custom clock abstractions.
- Use `System.Text.Json` for repository metadata formats.
- Use provider-owned string constants for durable JSON identifiers such as format names and object store kinds. Avoid C# enums for these values.
- Keep the base `YabtException` in `Yabt.Common`; each YABT assembly should expose its own `YabtXxxException` derived from it.
- When catching lower-level exceptions, catch the base `Exception` type directly and wrap it with the assembly-specific YABT exception only when the wrapper adds useful operation context such as object keys, metadata paths, provider operation names, or recovery state. Do not catch SDK- or provider-specific exception types such as Azure `RequestFailedException` solely to wrap them, and do not use exception filters as a fallback. This intentionally accepts deeper inner-exception chains when multiple layers add useful context.
- Do not replace scaffold `NotImplementedException` throws with YABT exceptions.
- When intentionally ignoring expected cleanup exceptions, log them at debug level instead of leaving an empty catch block.
- Favor deterministic, inspectable behavior over clever hidden state.
- Add abstractions when they protect architectural boundaries or simplify real complexity.
- Avoid speculative implementation beyond the requested scaffold.
- Put implementation classes and their satellite helper classes in a child namespace named `Implementation`.
- Implementation and satellite helper classes should be `internal` unless a stronger restriction is possible.
- Put extension method classes in the namespace of the extended type, such as `Microsoft.Extensions.DependencyInjection` for `IServiceCollection` extensions.
- Suppress `IDE0130` on extension method files whose namespace intentionally differs from the folder structure.
- Avoid duplicate class names even across different namespaces or projects. Prefer descriptive names such as `YabtSyncServiceCollectionExtensions`.
- Prefer providing defaults for optional parameters and record constructor parameters.
- Prefer nullable collection parameters when the collection is optional.
- Place `using` directives before the file-scoped namespace in all C# files.
- Prefer primary constructors where applicable. If a primary constructor parameter is used as the backing field, name it with the same underscore convention as a private field, for example `_logger`.
- Put the opening and closing parentheses on their own lines if the parentheses scope spans across multiple lines, including declarations, definitions, method calls, constructor calls, and record construction.
- Prefer `IEnumerable<T>` for collection parameters in record types unless a stronger read-only collection interface is specifically needed.
- Prefer `IEnumerable<T>` collection parameters over `params` arrays for helper methods unless the call-site ergonomics clearly justify `params`.
- Prefer collection expressions such as `[]` over `Array.Empty<T>()`, `Enumerable.Empty<T>()`, and similar empty collection helpers.
- Do not use fully qualified attribute names; add an appropriate `using` directive instead.
- In multiline expressions, keep operators at the end of the line rather than at the beginning of the continuation line, including null-coalescing, conditional, arithmetic, Boolean, and fluent-chain operators.
- Use target-typed `new` when the constructed concrete type is clear from context.
- Use frozen collections, such as `FrozenSet<T>` and `FrozenDictionary<TKey, TValue>`, for conceptually static or rarely rebuilt collections.
- Do not buffer an `IEnumerable<T>` or `IAsyncEnumerable<T>` with `ToArray()`, `ToList()`, or similar unless the result is enumerated multiple times, indexed, counted, or needs a stable snapshot.
- Use an intermediate local variable for complex `foreach` and `await foreach` source expressions.
- Keep each top-level type in a file named for that type.
- Prefer expression-bodied members for simple methods that only return one expression.
- Prefer `Yabt.Common.Check.NotNull()` for constructor null guards.
- For lambda parameters, choose names that make it hard to accidentally use an outer-scope value; when wrapping cancellation-aware callbacks, prefer the conventional `cancellationToken` name in a scope where it can be used unambiguously.
- Short single-statement `if` and `else` blocks may stay on one line when that is more readable.
- Always set a default for `CancellationToken` parameters in public methods.
- Omit cancellation token arguments when the called API provides a default and there is no meaningful token to pass.
- Pass `default` instead of `CancellationToken.None` when an explicit cancellation token argument is required and no real token is available.
- Cancellation of synchronous operations wrapped by `YabtTask.Run` cancels waiting, not the underlying operation. The abandoned operation may finish or fail later.
- Always observe exceptions from abandoned operations. Log them at debug level when they are otherwise ignored.
- Treat cancellation as an ordinary failure unless a caller explicitly needs to distinguish it. Use `IsCancellationException()` for that case.
- Do not log an exception at a layer that throws a new contextual exception containing it. Log failures only when they would otherwise be discarded.
- Chunked async enumeration may observe cancellation only at chunk boundaries. Buffered items may still be yielded after cancellation is requested.
- For classes that have an `ILogger`, add a simple `_logger.LogTrace(nameof(MethodName));` at the start of each method.
- Use source-generated `[LoggerMessage]` logging methods instead of direct `_logger.Log...()` calls whenever the direct call would trigger CA1873. Prefix generated logging methods with `Log`, implement them as `ILogger` extension methods, and call them like `_logger.LogSomething(...)`. Prefer focused internal partial logging helper classes near the consuming implementation.
- Define every `[LoggerMessage]` event ID as a named `const int` in `Yabt.Common.YabtEventIds` and reference that constant from the attribute. Keep all YABT event IDs centralized there; do not inline numeric event IDs in logging helpers.
- Keep an empty line after method declarations or definitions.
- Keep line endings consistent. Follow `.editorconfig` and `.gitattributes`; text files in this repository should use CRLF unless a file-specific rule says otherwise.
- Options classes should use nullable properties, including value types where applicable.
- Resolve options defaults in consumers or through explicit helper methods such as `GetEffective...`; do not hide defaults in non-null property initializers.
- Use the `Microsoft.Extensions.Options` pattern for configuration where possible.
- Service registration methods should accept an optional `string? configSectionPath = null` and bind options from that configuration section when provided.
- Consumers of configurable options should use `IOptionsMonitor<T>` when they need to observe runtime option changes.
- Do not register clients that depend on reloadable options as singletons if that would freeze old option values.

## Solution File Hygiene

When adding non-code files outside project folders that are not excluded by `.gitignore`, also add them to the Visual Studio solution as `Solution Items`.

If the files live under a repository folder such as `docs`, `examples`, or `spec`, mirror that folder structure under the `Solution Items` solution folder instead of flattening everything into the root.

Do not add files from `src` project folders to `Solution Items`. Add them to the corresponding `.csproj` only when they are not already included implicitly by the SDK.

Preserve Visual Studio-generated solution formatting and existing solution-folder GUIDs where possible.

## Configuration

Prefer the configuration defaults provided by `Host.CreateApplicationBuilder` and other framework builders. Do not explicitly clear or rebuild configuration sources unless the defaults are insufficient.

## Inline Review Markers

Review marker comments in code carry specific meanings:

- `//FIX:` means the agent should automatically perform the described fix when it sees the marker, without requiring a separate explicit user request.
- `//ASK:` means the agent should automatically explain the stated question when it sees the marker, without requiring a separate explicit user request.
- `//TODO` comments are intended for the human maintainer. Ignore them unless the user explicitly asks to address TODO comments.

## Review Workflow

The user is reviewing these changes as if they are a PR and will provide incremental feedback.

When addressing feedback:

- Treat the newest feedback as authoritative.
- Keep changes focused and easy to review.
- Do not rewrite unrelated files.
- Preserve user edits and Visual Studio-generated solution formatting.
- Summarize exactly what changed and what was verified.
- If a request affects architecture, update this file when the guidance should persist.
- Remove feedback comments when the requested change is fully implemented and verified, but keep the original feedback text in the commit message for historical context.
- Please leave git index untouched unless the user explicitly requests otherwise. If you need to change a file that is already staged, make the change but do not stage it. The user will review the change and stage it if they approve.
