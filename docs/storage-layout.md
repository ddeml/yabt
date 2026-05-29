# Storage Layout

Archive paths are ordinary object names with a root descriptor and two top-level data areas:

```text
/.yabt-root.json
/live/...
/hist/...
```

The leading slash is conceptual. Azure Blob names are stored without it, for example `live/photos/2026/image.jpg`. Filesystem and WebDAV providers should expose equivalent ordinary paths.

## Root Descriptor

`.yabt-root.json` identifies the root role, layout, and known object stores. It must not contain secrets.

## Data Areas

```text
/live/...
/hist/...
```

## Live

`/live` represents the current source filesystem state.

For folders using the `mirror` format, files are uploaded individually under the same relative path:

```text
/live/Documents/report.docx
/live/Photos/Vacation/img001.jpg
```

For folders using the `zip` format, the package artifact and adjacent manifest are visible objects:

```text
/live/Photos/Vacation/.yabt-policy.json
/live/Photos/Vacation/Vacation.20260524T120000Z.a91f3c2e.zip
/live/Photos/Vacation/Vacation.20260524T120000Z.a91f3c2e.manifest.json
```

The folder policy or equivalent descriptor remains outside the package so a browser or restore tool can identify the folder representation without opening the package first.

## Hist

`/hist` preserves obsolete, replaced, or deleted state.

The initial design favors copying old `/live` objects into `/hist` before replacing or removing live objects. The exact historical sublayout may evolve, but it should remain browsable and should avoid content loss.

Future deduplication, if implemented, may exist only under `/hist` and must use explicit reference placeholder JSON files. `/live` must not become a deduplicated block store.

## Browsability

Browsers such as the local filesystem, Azure Storage Explorer, or WebDAV clients should show meaningful folder and file names. Package files should be standard archive formats, and metadata files should be readable JSON.
