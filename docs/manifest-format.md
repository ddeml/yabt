# Manifest Format

Every package artifact should have two manifests:

- An adjacent JSON file in the archive target.
- An embedded JSON file inside the package.

The manifest describes what was packaged and makes archive inspection possible without a database. It is evidence of a concrete artifact, not root configuration or folder policy intent.

## Draft Shape

```json
{
  "sourcePath": "D:\\Photos\\Vacation",
  "createdAtUtc": "2026-05-24T12:00:00Z",
  "format": "zip",
  "files": [
    {
      "relativePath": "img001.jpg",
      "length": 4821031,
      "lastWriteTimeUtc": "2026-05-21T18:33:11Z",
      "contentHash": "sha256:..."
    }
  ],
  "totalBytes": 4821031,
  "manifestHash": "sha256:a91f3c2e...",
  "packageName": "Vacation.20260524T120000Z.a91f3c2e.zip"
}
```

## Hashing

The manifest hash should be computed over a deterministic canonical representation.

## Required Data

- Source path.
- Creation time in UTC.
- Archive format name.
- File list.
- File count, derived from the file list.
- Total byte count.
- Manifest hash.
- Package artifact name.
