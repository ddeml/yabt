# Agent Notes

This file records durable project guidance for Codex and other AI agents working in this repository. Treat it as standing context unless the user explicitly updates it.

## Project Goal

YABT is a .NET 10 filesystem-to-Azure-Blob archival synchronization tool.

It should replicate ordinary folders into Azure Blob Storage in a way that remains directly inspectable, understandable, and restorable without proprietary tooling. The project runs primarily on Windows at first, but architecture should remain portable and Linux-compatible where practical.

The system is a long-term durable archival replication tool, not a proprietary backup repository format.

## User Context

The human maintainer is a seasoned developer with 30+ years of experience, especially in C#/.NET, MS-SQL, Oracle, and related ecosystems.

Do not over-explain basic programming concepts. Prefer concise engineering tradeoffs, explicit assumptions, and clear implementation choices. When introducing AI-agent workflow suggestions, keep them practical and lightweight.

## Architectural Principles

- Azure Blob Storage must remain directly browsable with Azure Storage Explorer.
- Restore should be symmetrical to backup/sync.
- Blob layout should mirror the original folder hierarchy.
- The archive must remain understandable and restorable without proprietary tooling.
- Standard archive formats should be used: 7z, tar.gz, zip.
- Metadata should be stored in human-readable JSON files.
- SQLite may be used only as a disposable cache or performance optimization.
- SQLite must never be the authoritative source of truth.
- The filesystem plus metadata files are the source of truth.
- Azure Blob Storage is the durable replica/archive target.
- Prefer append-mostly behavior.

## Live And Hist

The blob layout conceptually uses:

```text
/live/...
/hist/...
```

`/live` represents current filesystem state.

`/hist` stores obsolete, replaced, or deleted historical state. Deleted or replaced content should generally move to `/hist` instead of being deleted.

Do not deduplicate `/live`.

Future deduplication may occur only under `/hist`, and only through explicit reference placeholder JSON files. Do not implement deduplication until requested.

## Packaging

Folders may optionally be packaged before upload. Packaging is controlled by metadata files inside folders.

The primary policy file name is:

```text
.backup-policy.json
```

Supported policy modes:

- `mirror`: upload files individually.
- `package`: package the folder into an archive before upload.
- `auto`: allow the tool to decide later.

Initial archive formats:

- `7z`
- `tar.gz`
- `zip`

Package artifacts should be immutable and named using:

```text
<folder-name>.<timestamp-utc>.<manifest-or-content-hash-prefix>.<extension>
```

Example:

```text
Vacation.20260524T120000Z.a91f3c2e.7z
```

Older package versions should remain preserved.

## Manifests

Each package should have:

- An adjacent manifest JSON file.
- An embedded manifest inside the archive.

Manifest data should include:

- Source path.
- Creation time.
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
- Microsoft.Data.Sqlite

Prefer async APIs throughout.

Enable nullable reference types and analyzers.

## Repository Structure

Expected high-level folders:

- `src`
- `tests`
- `docs`
- `spec`
- `examples`

Initial source projects:

- `Yabt.Core`
- `Yabt.AzureBlob`
- `Yabt.Packaging`
- `Yabt.Metadata`
- `Yabt.Sync`
- `Yabt.Cli`

Project boundaries may evolve, but keep storage adapters, domain models, metadata handling, packaging, sync orchestration, and CLI concerns separated.

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
- Keep SQLite cache concerns out of durable model code.
- Use async APIs for I/O and cloud operations.
- Use `System.TimeProvider` instead of custom clock abstractions.
- Use `System.Text.Json` for repository metadata formats.
- Favor deterministic, inspectable behavior over clever hidden state.
- Add abstractions when they protect architectural boundaries or simplify real complexity.
- Avoid speculative implementation beyond the requested scaffold.
- Place `using` directives before the file-scoped namespace in all C# files.
- Prefer primary constructors where applicable. If a primary constructor parameter is used as the backing field, name it with the same underscore convention as a private field, for example `_logger`.
- For method declarations with parameters split across multiple lines, put the opening and closing parentheses on their own lines.
- For records with primary constructor parameters split across multiple lines, put the opening and closing parentheses on their own lines.
- For multiline constructor calls, put the opening and closing parentheses on their own lines.
- Prefer `IEnumerable<T>` for collection parameters in record types unless a stronger read-only collection interface is specifically needed.
- Prefer collection expressions such as `[]` over `Array.Empty<T>()`, `Enumerable.Empty<T>()`, and similar empty collection helpers.
- Do not use fully qualified attribute names; add an appropriate `using` directive instead.
- In multiline expressions, keep operators at the end of the line rather than at the beginning of the continuation line.
- Use target-typed `new` when the constructed concrete type is clear from context.
- Use frozen collections, such as `FrozenSet<T>` and `FrozenDictionary<TKey, TValue>`, for conceptually static or rarely rebuilt collections.
- Always set a default for `CancellationToken` parameters in public methods.
- Omit cancellation token arguments when the called API provides a default and there is no meaningful token to pass.
- Pass `default` instead of `CancellationToken.None` when an explicit cancellation token argument is required and no real token is available.
- Keep an empty line after method declarations or definitions.
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

## Review Workflow

The user is reviewing these changes as if they are a PR and will provide incremental feedback.

When addressing feedback:

- Treat the newest feedback as authoritative.
- Keep changes focused and easy to review.
- Do not rewrite unrelated files.
- Preserve user edits and Visual Studio-generated solution formatting.
- Summarize exactly what changed and what was verified.
- If a request affects architecture, update this file when the guidance should persist.
