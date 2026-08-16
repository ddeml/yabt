# Manifest Format

YABT uses two distinct manifest concepts. The implemented root `.yabt-change-manifest.json` accelerates comparison of the current logical live branch. The package manifests described below are artifact-specific and remain planned work.

The root change manifest records each live-relative artifact path, actual stored length, optional UTC modification time, logical change fingerprint, and actual-byte xxHash128 content hash. It is deterministically ordered and has an xxHash128 self-hash. The self-hash detects accidental damage; it is not authentication against a malicious writer. This root document is comparison evidence rather than a restorable historical snapshot.

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
      "contentHash": "xxh128:..."
    }
  ],
  "totalBytes": 4821031,
  "manifestHash": "xxh128:a91f3c2e5b7d4f8096a1c3e8d2b4f607",
  "packageName": "Vacation.xxh128-a91f3c2e5b7d4f8096a1c3e8d2b4f607.zip"
}
```

## Hashing

The manifest hash should be computed over a deterministic canonical representation.

`createdAtUtc` records artifact metadata but is not part of the package filename. Once manifests are emitted, this value must remain stable for a particular content version rather than being regenerated on every projection; otherwise an embedded manifest would make unchanged ZIP bytes differ only because synchronization ran at a different time. Reprojecting the same logical representation therefore produces the same live key. YABT uses full xxHash128 values for the ZIP logical-source fingerprint, quick file-metadata fingerprints, actual-byte content hashes, and manifest self-hashes. These values share an algorithm but retain distinct meanings and domain/version tags where appropriate.

## Required Data

- Source path.
- Creation time in UTC.
- Archive format name.
- File list.
- File count, derived from the file list.
- Total byte count.
- Manifest hash.
- Package artifact name.
