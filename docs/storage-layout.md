# Storage Layout

Archive paths are ordinary object names with a root descriptor and two logical data branches:

```text
/.yabt-root.json
/.yabt-change-manifest.json
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

This keeps ordinary source folders rooted at their actual folder root. If a real data name would clash with `.yabt-root.json`, `.yabt-change-manifest.json`, `.yabt-policy.json`, `.yabt-tmp`, or the configured history prefix, initialize the root with different prefixes before using it.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root. They are not ordinary live data even though they physically sit under the same root.

`.yabt-change-manifest.json` always sits at the archive root, outside an explicit `livePrefix`. Its paths are logical live-relative paths. The name is reserved when the live prefix is empty, so an ordinary root source file with that exact name is excluded from live projection.

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

An empty folder may be preserved with the reserved marker file:

```text
EmptyFolder/.yabt-empty
```

The marker exists only to make an otherwise empty folder visible in object stores that cannot represent empty directories directly.

With an archive-style `livePrefix` of `live`, the same objects would appear as:

```text
live/Documents/report.docx
live/Photos/Vacation/img001.jpg
```

The intended layout for folders using the `zip` format keeps the package artifact and adjacent manifest as visible objects:

```text
Photos/Vacation.xxh128-a91f3c2e5b7d4f8096a1c3e8d2b4f607.zip
Photos/Vacation.xxh128-a91f3c2e5b7d4f8096a1c3e8d2b4f607.manifest.json
```

Here the source folder is `Photos/Vacation`, but its package objects are placed directly in the target `Photos` folder. No target `Photos/Vacation` folder is created. The folder policy or equivalent artifact-scoped descriptor remains outside the package, in the same parent folder, so a browser or restore tool can identify the folder representation without opening the package first.

The package name is deterministic for the projected representation, including archived metadata such as entry modification times. Package creation time belongs in the planned manifest instead of the live object name, so synchronizing an unchanged projected representation again resolves to the same key. A changed representation produces a different full-hash name, and the synchronizer moves the replaced name to history.

The current initial ZIP projector emits the `.zip` artifact only. The adjacent manifest and external descriptor shown above remain planned work.

## Hist

The logical history branch preserves obsolete, replaced, or deleted state. It is physically rooted at `histPrefix`.

The initial design moves old logical live state into the logical history branch before replacing or removing it. If a complete live folder becomes obsolete, for example during a `mirror` to `zip` transition, its complete folder or prefix representation moves to history together. This includes `.yabt-empty` markers and native empty descendants, so no empty live folder is left behind after a completed synchronization. The exact historical sublayout may evolve, but it should remain browsable and should avoid content loss.

Future deduplication, if implemented, may exist only under the logical history branch and must use explicit reference placeholder JSON files. The logical live branch must not become a deduplicated block store.

## Browsability

Browsers such as the local filesystem, Azure Storage Explorer, or WebDAV clients should show meaningful folder and file names. Package files should be standard archive formats, and metadata files should be readable JSON.
