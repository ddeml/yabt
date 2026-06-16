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

Archive-style roots may still configure explicit prefixes such as `livePrefix = "live"` and `histPrefix = "hist"` when that layout is preferable.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root and are not ordinary live data.

Do not deduplicate the logical live branch.

Future deduplication may occur only under the logical history branch, and only through explicit reference placeholder JSON files. Do not implement deduplication until requested.

## Archive Root Metadata

The archive root should contain a human-readable descriptor named:

```text
.yabt-root.json
```

This file identifies the archive, records layout information, and describes known object stores by provider-owned string names.

It may include an optional `rootRole` value such as `source` or `target` to indicate the intended default role of the root. The role is advisory and does not imply a physical layout; command direction still determines backup, restore, verification, or reconciliation behavior.

It may contain non-secret connection details such as container names, endpoints, prefixes, and credential references. It must not contain account keys, SAS tokens, passwords, client secrets, or other credentials.

Object store roles are operation-specific. A store may be a source, target, backup location, restore location, or reconciliation peer depending on the command.

Initial object store providers:

- `fileSystem`
- `azureBlob`
- `webDav`

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

Package artifacts should be immutable and named using:

```text
<folder-name>.<timestamp-utc>.<manifest-or-content-hash-prefix>.<extension>
```

Example:

```text
Vacation.20260524T120000Z.a91f3c2e.zip
```

Older package versions should remain preserved.

When a folder is packaged, the archive-side folder metadata should remain visible outside the package, near the package artifact and adjacent manifest. The source-side policy may live inside the source folder so it moves naturally with that folder.

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

## Change Detection

The system must eventually support scalable change detection for millions of files.

Architecture should allow future integration with:

- Synology btrfs snapshot diffing.
- Filesystem event monitoring.
- Manifest hashing.
- Incremental reconciliation.

Do not implement full scanning logic in the scaffold. Keep abstractions ready for future scalable delta discovery.

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

Future command surface:

- `sync`
- `restore`
- `scan`
- `verify`
- `pack`
- `reconcile`

Only scaffold command structure until sync semantics are designed.

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
