# Storage Layout

Blob paths are ordinary object names with two top-level areas:

```text
/live/...
/hist/...
```

The leading slash is conceptual. Azure Blob names are stored without it, for example `live/photos/2026/image.jpg`.

## Live

`/live` represents the current source filesystem state.

For mirror-mode folders, files are uploaded individually under the same relative path:

```text
/live/Documents/report.docx
/live/Photos/Vacation/img001.jpg
```

For package-mode folders, the package artifact and adjacent manifest are visible objects:

```text
/live/Photos/Vacation/Vacation.20260524T120000Z.a91f3c2e.7z
/live/Photos/Vacation/Vacation.20260524T120000Z.a91f3c2e.manifest.json
```

## Hist

`/hist` preserves obsolete, replaced, or deleted state.

The initial design favors copying old `/live` objects into `/hist` before replacing or removing live objects. The exact historical sublayout may evolve, but it should remain browsable and should avoid content loss.

Future deduplication, if implemented, may exist only under `/hist` and must use explicit reference placeholder JSON files. `/live` must not become a deduplicated block store.

## Browsability

Azure Storage Explorer should show meaningful folder and file names. Package files should be standard archive formats, and manifests should be readable JSON.
