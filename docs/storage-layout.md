# Storage Layout

Archive paths are ordinary object names with a root descriptor and two logical data branches:

```text
/.yabt-root.json
/.yabt-change-manifest.json.br
<livePrefix>/...
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

The logical change manifest always sits at the archive root, outside an explicit `livePrefix`. Brotli compression is the default, producing `.yabt-change-manifest.json.br`; `changeManifestCompression: "none"` produces `.yabt-change-manifest.json`. Its paths are logical live-relative paths. Both names are reserved when the live prefix is empty, so ordinary root source files with either exact name are excluded from live projection. Readers always inspect both representations. If both exist, they must validate to the same self-hash before either is trusted; the next successful mutating sync historizes the old representation or representations and leaves only the configured one live. During recovery from conflicting representations, `.yabt-change-manifest.invalid` may appear temporarily at the root. Its presence disables fast evidence until the replacement is complete; it is also reserved and moves to history last.

The filesystem provider uses the reserved `.yabt-tmp` directory to stage each upload before atomically moving the completed file to its final path. Each upload uses a unique temporary file and removes that file after success or a controlled failure. The shared directory intentionally remains so concurrent YABT processes have a stable staging location without racing directory creation against deletion; an empty directory simply means no upload is currently staged. A nonempty directory may belong to an active upload or contain evidence of an interrupted run, so synchronization ignores it as provider plumbing. YABT rejects live or history prefixes that overlap `.yabt-tmp`, including case variants, to keep staging separate from archive data.

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

Future deduplication, if implemented, may exist only under the logical history branch and must use explicit reference placeholder JSON files. The logical live branch must not become a deduplicated block store.

## Browsability

Browsers such as the local filesystem, Azure Storage Explorer, or WebDAV clients should show meaningful folder and file names. Package files should be standard archive formats, and metadata files should be readable JSON.
