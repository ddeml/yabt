# Manifest Format

YABT uses two distinct manifest concepts. The implemented root change manifest accelerates comparison of the current logical live branch. It is logical JSON stored by default as standard Brotli-compressed `.yabt-change-manifest.json.br`, or as plain `.yabt-change-manifest.json` when root configuration selects no compression. The package manifests described below are artifact-specific and remain planned work.

The root change manifest records each live-relative artifact path, logical change fingerprint, and actual-byte xxHash128 content hash. Ordinary file fingerprints use the readable `stat-v1:<UTC timestamp>:<length>` form, so their length and modification time are not duplicated as separate fields. An optional `artifactLength` is present only when a projector cannot report the produced object's length, such as for a lazily built ZIP; it lets quick verification detect target truncation without rebuilding or reading the package. xxHash128 values use canonical unpadded Base64URL. The logical JSON manifest is deterministically ordered and has an xxHash128 self-hash; compression does not change that hash. The self-hash detects accidental damage; it is not authentication against a malicious writer. This root document is comparison evidence rather than a restorable historical snapshot. Readers inspect both storage filenames, while a successful mutating sync leaves only the representation selected by `changeManifestCompression`. A transient `.yabt-change-manifest.invalid` marker makes all manifest evidence untrusted until an interrupted or conflicting multi-file replacement finishes safely.

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
      "contentHash": "xxh128:qR88Llt9T4CWocPo0rT2Bw"
    }
  ],
  "totalBytes": 4821031,
  "manifestHash": "xxh128:qR88Llt9T4CWocPo0rT2Bw",
  "packageName": "Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.zip"
}
```

## Hashing

The manifest hash should be computed over a deterministic canonical representation.

`createdAtUtc` records artifact metadata but is not part of the package filename. Once manifests are emitted, this value must remain stable for a particular content version rather than being regenerated on every projection; otherwise an embedded manifest would make unchanged ZIP bytes differ only because synchronization ran at a different time. Reprojecting the same logical representation therefore produces the same live key. YABT uses full Base64URL-encoded xxHash128 values for the ZIP logical-source fingerprint, actual-byte content hashes, and manifest self-hashes. Package file names encode that same 128-bit value as lowercase Base32hex so names remain stable on case-insensitive file systems. Quick file-metadata fingerprints instead expose their UTC timestamp and length directly. These values retain distinct meanings and domain/version tags where appropriate.

## Required Data

- Source path.
- Creation time in UTC.
- Archive format name.
- File list.
- File count, derived from the file list.
- Total byte count.
- Manifest hash.
- Package artifact name.
