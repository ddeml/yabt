# Storage Layout

Archive paths are ordinary object names with a root descriptor and two logical data branches:

```text
/.yabt-root.json
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

This keeps ordinary source folders rooted at their actual folder root. If a real data name would clash with `.yabt-root.json`, `.yabt-policy.json`, or the configured history prefix, initialize the root with different prefixes before using it.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root. They are not ordinary live data even though they physically sit under the same root.

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

With an archive-style `livePrefix` of `live`, the same objects would appear as:

```text
live/Documents/report.docx
live/Photos/Vacation/img001.jpg
```

For folders using the `zip` format, the package artifact and adjacent manifest are visible objects:

```text
Photos/Vacation/.yabt-policy.json
Photos/Vacation/Vacation.20260524T120000Z.a91f3c2e.zip
Photos/Vacation/Vacation.20260524T120000Z.a91f3c2e.manifest.json
```

The folder policy or equivalent descriptor remains outside the package so a browser or restore tool can identify the folder representation without opening the package first.

## Hist

The logical history branch preserves obsolete, replaced, or deleted state. It is physically rooted at `histPrefix`.

The initial design favors copying old logical live objects into the logical history branch before replacing or removing live objects. The exact historical sublayout may evolve, but it should remain browsable and should avoid content loss.

Future deduplication, if implemented, may exist only under the logical history branch and must use explicit reference placeholder JSON files. The logical live branch must not become a deduplicated block store.

## Browsability

Browsers such as the local filesystem, Azure Storage Explorer, or WebDAV clients should show meaningful folder and file names. Package files should be standard archive formats, and metadata files should be readable JSON.
