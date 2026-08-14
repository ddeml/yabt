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
  "manifestHash": "sha256:a91f3c2e5b7d4f8096a1c3e8d2b4f607c84d2a1e7395b60f4c8e2d1a7b9306f5",
  "packageName": "Vacation.sha256-a91f3c2e5b7d4f8096a1c3e8d2b4f607c84d2a1e7395b60f4c8e2d1a7b9306f5.zip"
}
```

## Hashing

The manifest hash should be computed over a deterministic canonical representation.

`createdAtUtc` records artifact metadata but is not part of the package filename. Once manifests are emitted, this value must remain stable for a particular content version rather than being regenerated on every projection; otherwise an embedded manifest would make unchanged ZIP bytes differ only because synchronization ran at a different time. Reprojecting the same logical representation therefore produces the same live key. The initial ZIP projector uses the full 128-bit xxHash value of its logical source manifest; a future canonical hashing design may select a different explicitly named algorithm.

## Required Data

- Source path.
- Creation time in UTC.
- Archive format name.
- File list.
- File count, derived from the file list.
- Total byte count.
- Manifest hash.
- Package artifact name.
