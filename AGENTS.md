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

Archive-style roots may still configure explicit prefixes such as `livePrefix = "live"` and `histPrefix = "hist"` when that layout is preferable.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root and are not ordinary live data.

YABT reserves `.yabt-tmp` at the archive root for provider/runtime plumbing. The filesystem provider uses it for same-filesystem upload staging, and archive-mutating commands use `.yabt-tmp/archive-mutation-lock.json` to coordinate access. Filesystem conditional mutations also serialize through an empty `conditional-mutation.lock`, which may remain while no command is active. Each filesystem upload should use a uniquely named temporary file, remove that file after the completed file is atomically moved into place or after a controlled failure, and leave the shared staging directory in place. Deleting and recreating the shared directory can race concurrent YABT processes. Other nonempty contents may belong to an active upload or an interrupted run. Reject live or history prefixes that overlap `.yabt-tmp`, including case variants, so plumbing can never mix with archive data.

Do not deduplicate the logical live branch.

History deduplication is an explicit maintenance operation performed by the `deduplicate` command. `sync` must not scan or compare history for duplicates. The command accepts an archive root, optional target store id, and optional dry-run flag; byte-for-byte confirmation is mandatory and is not an optional comparison mode.

The history branch has its own durable `.yabt-history-manifest.json`, separate from the live change manifest. It records every historical occurrence and whether that occurrence is materialized or represented by a deduplication reference. The history manifest is a rebuildable index and must never contain exclusive information. Materialized entries contain only information that can be recovered by scanning or opening their actual objects, while each reference repeats its complete occurrence entry.

Deduplication may replace only historical objects. Leave one stable materialized historical copy for every deduplicated content hash. The current command scans only history and does not use or modify matching live objects; a future resolver may treat live objects as optional extra candidates, but never as the sole backing copy for historical references. References identify content by hash rather than by another reference path, so reference chains are impossible and a backing object's path can change without rewriting every reference.

Name a reference by appending `.yabt-ref.json` to the complete original filename, including its extension, for example `report.pdf.yabt-ref.json`. The reference must be human-readable, versioned JSON with document type `yabt.historyContentReference`, include the exact complete occurrence entry recorded in the history manifest, and protect that entry with a self-hash. History entries record the logical relative path, stored relative path, representation, content hash, content length, and optional observable last-modified time, content type, and provider metadata. Do not put a source-only change fingerprint or other unrebuildable information in the history manifest.

If that stored reference name is already occupied, append a deterministic numeric sequence before `.yabt-ref.json`, for example `report.pdf.1.yabt-ref.json`. Never overwrite an existing historical object to obtain the preferred reference name.

Treat an equal length and xxHash128 value as a duplicate candidate only. Compare complete byte streams before removing a redundant materialization. Establish and verify the stable materialized copy and durably write its reference metadata before deleting the redundant bytes. An interrupted command may leave an unnecessary full copy but must never leave a reference without a materialized backing object. Deduplication and other archive-mutating commands must not mutate the same archive concurrently.

The history catalog uses `.yabt-history-manifest.invalid` only while its current contents must not be trusted. When a successful `sync` adds or changes history, it deletes the now-stale `.yabt-history-manifest.json` and then deletes the invalidation marker before completing. A successful `deduplicate` removes the marker only after references, the replacement catalog, and guarded deletions complete. The marker may remain after an interrupted `sync` or `deduplicate`, but it must not remain after either command succeeds. Do not add legacy cleanup that searches history for manifests or markers historized by earlier MVP builds unless Leo explicitly requests it. YABT's archive lock coordinates YABT writers; users and other programs must not modify the archive while an archive-mutating command is active.

The optional root setting `historyDeduplicationTinyFileMaximumBytes` defaults to 4096. Objects at or below the effective threshold are not eligible for deduplication. Also retain a materialized object when its reference and index overhead would not produce a net storage saving. Exclude YABT metadata, `.yabt-empty`, reference files, manifests, and provider plumbing from deduplication.

## Archive Root Metadata

The archive root should contain a human-readable descriptor named:

```text
.yabt-root.json
```

This file identifies the archive, records layout information, and describes known object stores by provider-owned string names.

It may include an optional `rootRole` value such as `source` or `target` to indicate the intended default role of the root. The role is advisory and does not imply a physical layout; command direction still determines backup, restore, verification, or reconciliation behavior.

It may include an optional `defaultStoreId` value to select the default object store when a command does not explicitly specify one. If neither command input nor root metadata selects a store, use the first configured store and warn when multiple stores are available.

It may contain non-secret connection details such as container names, endpoints, prefixes, and credential references. It must not contain account keys, SAS tokens, passwords, client secrets, or other credentials.

Object store roles are operation-specific. A store may be a source, target, backup location, restore location, or reconciliation peer depending on the command.

Initial object store providers:

- `fileSystem`
- `azureBlob`
- `webDav`

## Object Store Traversal

`IObjectStore` should expose folder-local traversal rather than recursive flat listing. The object store contract should answer "what files and immediate child folders are inside this folder prefix?" so sync can compare source and target incrementally and future traversal can parallelize child folders.

Providers that do not have real directories, such as Azure Blob Storage, should emulate immediate child folders from object key prefixes. Do not rely on global ordering of recursive object listings for sync correctness.

The filesystem provider must not traverse directory reparse points or symbolic-link directories below an object-store root. Following one during destructive history maintenance could escape the configured archive root. A linked directory that is intentionally an archive root should be configured as the root itself instead.

Empty folders may be represented with the reserved marker file:

```text
.yabt-empty
```

The current `mirror` projection creates this marker for every empty folder on every target, including filesystem targets, because the projection contract emits objects rather than native folder-creation operations. The marker remains in the logical live branch while the folder is empty, moves to history when that folder gains content or changes format, and is hidden from normal YABT traversal. Treat it as durable YABT folder plumbing, not ordinary user data or temporary cleanup residue.

## Formats And Packaging

Folders may optionally be packaged before upload. Packaging is controlled by metadata files inside folders.

The primary policy file name is:

```text
.yabt-policy.json
```

The policy file should use one provider-owned string value named `format`. Do not model durable format names as C# enums.

Initial archive format projectors:

- `mirror`
- `zip`

Do not differentiate durable `ArchiveFormat` and `PackageMode` concepts. Different folder representations are archive formats. Do not implement an `auto` format initially.

Each archive format projector owns its format name and projects a source folder plus policy into an intended archive representation. Historization, target comparison, and delete handling belong to the archive synchronizer, not to the format projector.

The format projection contract is `IArchiveFormatProjector`. Do not keep a central format registry in `Yabt.Core`.

Package artifacts should be immutable and use deterministic content- or manifest-addressed names:

```text
<folder-name>.<hash-algorithm>-<full-manifest-or-content-hash>.<extension>
```

Example:

```text
Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.zip
```

Do not put the projection or package creation time in the package artifact name. Creation time belongs in the manifest, while synchronization history paths record when a live artifact was replaced. Any creation time embedded inside a package must be stable for that content version rather than regenerated on every projection, so it does not make the package vary on every run. YABT-owned JSON hash values use the full 128-bit xxHash128 value encoded as canonical unpadded Base64URL. Package file names encode the same full value as lowercase unpadded Base32hex so their identity remains stable on case-insensitive file systems. The current ZIP projector hashes a logical representation containing ordered source paths, archived lengths and modification times, versioned per-object change fingerprints, and output-affecting compression settings. A normal file fingerprint stores its known length and exact UTC modification time directly in a canonical human-readable string; incomplete metadata falls back to a provider content hash or a byte scan. Do not truncate the aggregate hash to an arbitrary short prefix. An unchanged logical source manifest should produce the same live object key, while changed logical source manifests produce new immutable keys. Older package versions should remain preserved in history. Provider-supplied checksums may use provider-owned algorithms and must not be treated as comparable unless their algorithm is known.

When a subfolder is packaged, place its package artifact and adjacent metadata directly in the logical parent folder. Do not create a target folder corresponding to the packaged source folder. Packaging the selected root still places its artifacts at the target live root.

The archive-side folder metadata should remain visible outside the package, in the same parent target folder as the package artifact and adjacent manifest. The source-side policy may live inside the source folder so it moves naturally with that folder.

## Manifests

Each package should have:

- An adjacent manifest JSON file.
- An embedded manifest inside the archive.

Manifest data should include:

- Source path.
- Creation time.
- Archive format name.
- File list.
- File count.
- Total bytes.
- Manifest hash.
- Archive format.

Manifest JSON should be human-readable and deterministic once canonicalization is implemented.

## Metadata

Per-folder metadata files define intent and configuration, not operational state.

Metadata files should move with folders and survive reorganization.

Operational state belongs in disposable cache only.

The root change manifest is durable, human-readable comparison evidence rather than operational state or a disposable cache. Its logical content is JSON and it may be stored as `.yabt-change-manifest.json.br` using standard Brotli compression or as plain `.yabt-change-manifest.json`. It records live-relative artifact fingerprints and actual-byte content hashes. Do not duplicate a normal file's length or modification time outside its readable `stat-v1` fingerprint. Record a separate optional artifact length only when the projector cannot report the produced object's length, such as for a lazily built ZIP; this preserves quick target-truncation detection. Do not use target-native modification time as a substitute for the persisted source-derived fingerprint.

During MVP development, accept only the exact current metadata filenames, schema versions, and hash formats. Do not add legacy aliases, backward-compatibility readers, or migration paths unless Leo explicitly requests them.

## Change Detection

Normal `sync` and `verify` use the root change manifest and versioned metadata fingerprints to avoid opening unchanged objects. A normal file fingerprint is the human-readable `stat-v1:<UTC timestamp>:<length>` string. Keep `ChangeFingerprint` distinct from `ContentHash`: the fingerprint records quick-change metadata while the xxHash128 content hash covers the actual projected artifact bytes. xxHash128 provides fast accidental-change detection with a very low probability of an accidental collision; it is not intended as a cryptographic or adversarial integrity guarantee.

Do not store literal file contents in change manifests, even when a file is shorter than its hash. Small files may contain passwords, tokens, or other secrets, and durable metadata must remain comparison evidence rather than backup content.

Metadata fingerprints are not content proof. Same-length data whose timestamp was preserved, and same-length target corruption, may pass a quick check. `sync --byte-for-byte` and `verify --byte-for-byte` must bypass the fast match and compare complete streams. Missing timestamps, incomplete target length metadata, absent manifest evidence, or changed fingerprints must fall back to stream comparison. Do not use the ZIP-safe 1980 timestamp fallback as a general change-detection timestamp.

A mutating sync that falls back to stream comparison must load each projected source object only once. Retain the exact compared materialization in private delete-on-close local staging and upload that snapshot if it differs; do not reopen the source after comparison. This staging is ephemeral operation state, not archive metadata or a durable cache, and it must not use the target provider's `.yabt-tmp` namespace. Processed objects are staged individually, so the current sequential synchronizer needs local temporary space for at most one compared projected object at a time. Verification and dry runs do not need a replay copy because they never upload. When ZIP input metadata is too incomplete to identify an entry without reading its bytes, calculate that entry fingerprint while copying the same read into the ZIP instead of scanning and reopening the source file. The current ZIP projector is memory-backed; an eagerly built fallback package retains one backing buffer so it can be replayed without rereading its source files. Do not add another full-package copy. A future large-package implementation should introduce an explicitly owned, spillable projected-content lifetime.

Store the live change manifest at the archive root, outside an explicit `livePrefix`, with logical live-relative entry paths. `changeManifestCompression` in `.yabt-root.json` accepts `brotli` or `none` and defaults to `brotli`. Writers leave exactly one configured live representation, but readers always inspect both `.yabt-change-manifest.json.br` and `.yabt-change-manifest.json`. When both exist, trust them only if both validate and have the same logical self-hash; otherwise require full comparison. Treat both names and `.yabt-change-manifest.invalid` as reserved internal metadata when `livePrefix` is empty. The invalidation marker protects recovery while untrusted representations are being discarded; readers must not trust manifest evidence while it exists. Delete every obsolete root change-manifest representation instead of moving it to history, write the replacement last, and delete the root invalidation marker last. A successful mutating sync must never historize a root change manifest or its invalidation marker. Do not add legacy cleanup that scans history for such metadata left by earlier MVP builds unless Leo explicitly requests it. A mutating sync should discard and rebuild invalid or conflicting manifests; byte-for-byte comparison must remain usable without trusting them.

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

- `sync`
- `restore`
- `scan`
- `verify`
- `pack`
- `reconcile`
- `deduplicate`

`sync`, `verify`, and history-only `deduplicate` are implemented. Keep restore, scan, pack, and reconcile behavior scaffolded until their semantics are designed.

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
