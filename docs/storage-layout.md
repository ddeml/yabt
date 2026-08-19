# Storage Layout

Archive paths are ordinary object names with a root descriptor and two logical data branches:

```text
/.yabt-root.json
/.yabt-change-manifest.json.br
<livePrefix>/...
<histPrefix>/.yabt-history-manifest.json
<histPrefix>/...
```

The leading slash is conceptual. Azure Blob names are stored without it, for example `photos/2026/image.jpg` when `livePrefix` is empty or `live/photos/2026/image.jpg` when `livePrefix` is `live`. Filesystem and WebDAV providers should expose equivalent ordinary paths.

## Root Descriptor

`.yabt-root.json` identifies the root role, layout, and known object stores. It must not contain secrets.

The configured layout maps logical branches to physical prefixes:

- `livePrefix`: current logical state.
- `histPrefix`: obsolete, replaced, or deleted historical state.

The default layout is:

```text
livePrefix = ""
histPrefix = ".yabt-hist"
```

This keeps ordinary source folders rooted at their actual folder root. If a real data name would clash with `.yabt-root.json`, `.yabt-change-manifest.json.br`, `.yabt-change-manifest.json`, `.yabt-change-manifest.invalid`, `.yabt-policy.json`, `.yabt-tmp`, or the configured history prefix, initialize the root with different prefixes before using it.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root. They are not ordinary live data even though they physically sit under the same root.

The logical change manifest always sits at the archive root, outside an explicit `livePrefix`. Brotli compression is the default, producing `.yabt-change-manifest.json.br`; `changeManifestCompression: "none"` produces `.yabt-change-manifest.json`. Its paths are logical live-relative paths. Both names are reserved when the live prefix is empty, so ordinary root source files with either exact name are excluded from live projection. Readers always inspect both representations. If both exist, they must validate to the same self-hash before either is trusted. A successful mutating sync deletes obsolete representations and leaves only the configured one live. During recovery from conflicting representations, `.yabt-change-manifest.invalid` may appear temporarily at the root. Its presence disables fast evidence until replacement is complete, and a successful sync deletes it last. Root change manifests and their invalidation marker are internal comparison metadata and are never moved to history.

YABT reserves `.yabt-tmp` for provider/runtime plumbing. The filesystem provider stages each upload there before atomically moving the completed file to its final path. Each upload uses a unique temporary file and removes that file after success or a controlled failure. Archive-mutating commands also coordinate through `.yabt-tmp/archive-mutation-lock.json`; on filesystem targets the lock is held by an exclusive file handle, while remote providers use conditional object replacement. Filesystem conditional object mutations share an empty `conditional-mutation.lock`, which may remain present while idle. The shared directory intentionally remains so concurrent processes do not race directory creation against deletion. YABT ignores it during synchronization and rejects live or history prefixes that overlap it, including case variants.

An archive target may still use explicit branch directories:

```text
livePrefix = "live"
histPrefix = "hist"
```

The `rootRole` value is advisory. It does not imply a layout; commands and configured prefixes determine how the root is used.

## Live

The logical live branch represents current source filesystem state. It is physically rooted at `livePrefix`.

For folders using the `mirror` format, files are uploaded individually under the same relative path:

```text
Documents/report.docx
Photos/Vacation/img001.jpg
```

The current mirror representation preserves every empty folder with the reserved marker file:

```text
EmptyFolder/.yabt-empty
```

The object-only projection uses the marker on every target, including filesystems, so the same archive representation works across providers. It stays in live while the folder is empty and moves to history when the folder gains content or changes format. YABT hides it from its own normal traversal, but ordinary browsing tools may display it.

With an archive-style `livePrefix` of `live`, the same objects would appear as:

```text
live/Documents/report.docx
live/Photos/Vacation/img001.jpg
```

The intended layout for folders using the `zip` format keeps the package artifact and adjacent manifest as visible objects:

```text
Photos/Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.zip
Photos/Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.manifest.json
```

Here the source folder is `Photos/Vacation`, but its package objects are placed directly in the target `Photos` folder. No target `Photos/Vacation` folder is created. The folder policy or equivalent artifact-scoped descriptor remains outside the package, in the same parent folder, so a browser or restore tool can identify the folder representation without opening the package first.

The package name is deterministic for the projected representation, including archived metadata such as entry modification times. Its full xxHash128 value uses lowercase unpadded Base32hex in the file name so the token is stable on case-insensitive file systems. Package creation time belongs in the planned manifest instead of the live object name, so synchronizing an unchanged projected representation again resolves to the same key. A changed representation produces a different full-hash name, and the synchronizer moves the replaced name to history.

The current initial ZIP projector emits the `.zip` artifact only. The adjacent manifest and external descriptor shown above remain planned work.

## Hist

The logical history branch preserves obsolete, replaced, or deleted state. It is physically rooted at `histPrefix`.

The initial design moves old logical live state into the logical history branch before replacing or removing it. If a complete live folder becomes obsolete, for example during a `mirror` to `zip` transition, its complete folder or prefix representation moves to history together. This includes `.yabt-empty` markers and native empty descendants, so no empty live folder is left behind after a completed synchronization. The exact historical sublayout may evolve, but it should remain browsable and should avoid content loss.

The history branch may contain `.yabt-history-manifest.json`, which catalogs every non-control historical occurrence and marks it as materialized or deduplicated. This manifest is separate from the live change manifest and contains no exclusive information; YABT can rebuild it from the historical objects and their durable metadata. When a successful sync changes history, it deletes the stale catalog and then `.yabt-history-manifest.invalid`, leaving neither behind. The next `deduplicate` run rebuilds the catalog. During deduplication the marker protects the catalog transaction and is removed last; its presence after a command ends indicates that sync or deduplication was interrupted.

The `deduplicate` maintenance command may replace a redundant historical object with a self-describing JSON reference. The reference name appends `.yabt-ref.json` to the complete original filename so its former type remains visible:

```text
.yabt-hist/20260819T120000Z/Documents/report.pdf.yabt-ref.json
```

If that name is already occupied, YABT preserves it and uses a numbered name such as `report.pdf.1.yabt-ref.json`.

The reference repeats all occurrence metadata recorded in the history manifest and identifies its bytes by content hash. It does not point to another reference. Every referenced content hash retains one stable materialized copy in history. The current command does not scan matching live objects; they remain ordinary, untouched files.

Files at or below the configured tiny-file maximum remain materialized, as do files for which a reference would not save space. YABT metadata, `.yabt-empty`, history reference files, manifests, and provider staging objects are never deduplication candidates. A candidate with a matching length and xxHash128 value is replaced only after a complete byte comparison succeeds.

The archive mutation lock coordinates YABT commands, not arbitrary external writers. Ordinary tools may browse the archive while maintenance runs, but they must not change archive objects until the mutating command finishes.

Current MVP commands do not search history for change manifests or invalidation markers written there by earlier builds. Those files are not part of the current cleanup transaction.

## Browsability

Browsers such as the local filesystem, Azure Storage Explorer, or WebDAV clients should show meaningful folder and file names. Package files should be standard archive formats, and metadata files should be readable JSON.
