# Manifest Format

YABT uses three distinct manifest concepts. The implemented root change manifest accelerates comparison of the current logical live branch. The history manifest catalogs historical occurrences for inspection, future restore, and history-only deduplication. Package manifests are artifact-specific and remain planned work.

The root change manifest records each live-relative artifact path, logical change fingerprint, and actual-byte xxHash128 content hash. Ordinary file fingerprints use the readable `stat-v1:<UTC timestamp>:<length>` form, so their length and modification time are not duplicated as separate fields. An optional `artifactLength` is present only when a projector cannot report the produced object's length, such as for a lazily built ZIP; it lets quick verification detect target truncation without rebuilding or reading the package. xxHash128 values use canonical unpadded Base64URL. The logical JSON manifest is deterministically ordered and has an xxHash128 self-hash; compression does not change that hash. The self-hash detects accidental damage; it is not authentication against a malicious writer. This root document is comparison evidence rather than a restorable historical snapshot. Readers inspect both storage filenames, while a successful mutating sync deletes every obsolete representation and leaves only the representation selected by `changeManifestCompression`. A transient `.yabt-change-manifest.invalid` marker makes all manifest evidence untrusted while replacement is incomplete. A successful sync deletes that marker last. Root change manifests and their invalidation marker are never moved into history.

## History Manifest And References

The history manifest is `.yabt-history-manifest.json` under the configured history prefix. Its document type is `yabt.historyManifest`, and it records every non-control historical occurrence as either materialized or represented by a deduplication reference. Each entry contains its logical relative path, stored relative path, representation, content length, xxHash128 content hash, and optional observable last-modified time, content type, and provider metadata. A deterministic self-hash detects accidental changes to the manifest. `.yabt-history-manifest.invalid` marks the catalog as untrusted only during a mutation or after an interrupted one. When a successful sync changes history, it deletes the stale history manifest and then the marker; the next `deduplicate` run rebuilds the catalog. A successful `deduplicate` publishes the rebuilt document and removes its transaction marker last.

The manifest is a rebuildable catalog, not an exclusive source of archive truth. A materialized entry contains only information YABT can recover by scanning or opening the actual object. A reference embeds its exact complete manifest entry. Losing the history manifest may require opening and hashing the materialized objects again, but must not make any historical object unidentifiable or unrestorable. Source-only change fingerprints and other unrebuildable values do not belong in this catalog.

A reference is versioned JSON with document type `yabt.historyContentReference`. Its filename is the complete original filename followed by `.yabt-ref.json`, such as `report.pdf.yabt-ref.json`. If that stored name exists, YABT uses a numeric fallback such as `report.pdf.1.yabt-ref.json` rather than overwriting it. The reference embeds its exact history entry and protects that entry with a deterministic self-hash. It identifies content by hash rather than pointing to another reference path. This prevents reference chains and allows the stable materialized backing object's location to be selected or recovered independently.

At least one stable materialized historical object must remain for every hash used by a reference. The current deduplication command scans only history. A future resolver could consider matching live objects as extra candidates, but a historical reference must remain restorable after those live objects change or disappear.

During MVP development, YABT applies these rules only to the exact current metadata paths. It does not search history for change manifests or invalidation markers placed there by older builds.

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
