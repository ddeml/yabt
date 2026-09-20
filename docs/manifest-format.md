# Manifest Formats

YABT currently uses four distinct manifest concepts:

- The root live change manifest describes the archive's current projected artifacts.
- A package manifest describes one reversible ZIP projection and exists both beside and inside that ZIP.
- The destination logical-state manifest records rebuildable quick evidence after restore.
- The history manifest catalogs historical occurrences and deduplication references.

These JSON documents are durable, inspectable evidence. They do not replace the actual files and package objects as the source of truth, and their xxHash128 self-hashes detect accidental damage rather than malicious modification.

## Live Change Manifest

The live change manifest is stored at the archive root, outside an explicit `livePrefix`. Its configured representation is either Brotli-compressed `.yabt-change-manifest.json.br` or plain `.yabt-change-manifest.json`. Both contain the same logical JSON document and therefore have the same logical self-hash.

Current writers use document type `yabt.changeManifest` and schema version 3. The top-level fields are:

- `documentType`
- `schemaVersion`
- `rootFormat`
- Optional paired `rootDescriptorContentHash` and `rootDescriptorContentLength`
- Canonically ordered `entries`
- `manifestHash`

The root-descriptor evidence binds the exact validated `.yabt-root.json` bytes carried to the archive. The hash and length must either both be present or both be absent. Normal filesystem-backed source discovery supplies both.

Each entry contains:

- `relativePath`: the live-relative stored artifact path.
- `changeFingerprint`: source-derived quick evidence, such as `stat-v1:<UTC timestamp>:<length>`.
- Optional `artifactLength`: used when the format handler could not report the produced object's length before materialization.
- `contentHash`: the actual stored artifact's canonical xxHash128 value, required by schema version 3. Earlier compatibility schemas may omit it, but such incomplete evidence is never written by a current backup.
- Optional `projection`: durable grouping evidence for a format artifact.

A `projection` contains `logicalPath`, `format`, `formatVersion`, `projectionId`, and `artifactRole`. The ZIP handler emits a `package` role and a `manifest` role with the same other values. Restore groups artifacts by the first four fields and passes the complete group to the owning format handler. The logical path is relative to the selected archive root, so nested package projections remain unambiguous even when they are stored inside another package.

The reader retains two explicit development-build compatibility paths:

- Schema version 1 has neither `rootFormat` nor projection or root-descriptor evidence. Restore accepts it only when the root representation is unambiguous.
- Schema version 2 requires `rootFormat` but predates projection and root-descriptor evidence.

A successful current backup rewrites either older logical document as schema version 3. No other legacy names or schema aliases are accepted during MVP development.

Readers inspect both storage filenames. If both exist, YABT trusts them only when both validate and have the same logical self-hash. Before a mutating backup discards stale or conflicting representations it creates `.yabt-change-manifest.invalid`; the marker disables all fast evidence until the replacement is safely published. A successful backup deletes obsolete representations and removes the marker last. Neither the manifest nor its marker is historized.

## Package Manifest

Every current ZIP projection produces two byte-identical copies of one package manifest:

- Adjacent object: `<complete-package-filename>.yabt-manifest.json`
- Embedded ZIP entry: `.yabt-package-manifest.json`

For example:

```text
Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.zip
Vacation.xxh128-l4fjobirfl7o15l1ofkd5d7m0s.zip.yabt-manifest.json
```

The adjacent copy makes the projection understandable and lets restore build a lazy plan without downloading the ZIP. If any payload from that ZIP must be written, the handler validates the complete package, compares the embedded manifest bytes with the adjacent copy, and validates each staged payload against its entry before that file is committed. Package staging is released when its last dependent output has been prepared. A directory-only package is validated eagerly because it has no file stream that could otherwise trigger lazy validation; a zero-entry ZIP manifest is invalid.

The package and adjacent manifest form one backup consistency unit. If either artifact is new or changed, backup revalidates a sibling that matched only quick evidence against the same materialized projection. The live change manifest is published only after both artifacts describe the same generation.

The current ZIP handler limits each serialized package manifest to 16 MiB. Backup checks the limit before it can return package content, and restore applies the same bound to both copies. This prevents backup from publishing a package that its matching restore implementation can never read.

Package manifests use document type `yabt.packageManifest` and schema version 1. Their fields are:

- `sourcePath`: root-relative logical folder path; an empty string means the selected root.
- `createdAtUtc`: deterministic UTC creation metadata, equal to the latest exact entry timestamp.
- `format` and positive `formatVersion`.
- `projectionId`: the canonical xxHash128 logical projection identity.
- `packageName`: the filename only, without a folder path.
- `policy`: the canonical parsed folder-policy snapshot.
- Canonically ordered `entries`.
- `totalBytes`: the checked sum of entry lengths.
- `manifestHash`: the canonical xxHash128 self-hash.

There is no stored `fileCount`; it is derived from `entries`. The manifest also deliberately omits the completed ZIP's byte hash, because embedding that value in the ZIP would create a hash cycle. The root live change manifest binds the actual bytes of both the package and the adjacent manifest.

Each package entry has `kind`, `relativePath`, `storedPath`, `length`, exact `lastModifiedUtc`, and actual-byte `contentHash`. Supported kinds are:

- `file`: an ordinary payload file.
- `directory`: a zero-byte `.yabt-empty` payload whose logical path is restored as a native directory.
- `formatArtifact`: an artifact belonging to a nested format projection; it additionally requires `projection` with the same five fields used by the root live change manifest.

The exact `.yabt-policy.json` file remains a normal payload object in its own folder representation and therefore returns on restore. The separate `policy` field is the canonical intent snapshot used for artifact identity and inspection.

An abbreviated manifest looks like this:

```json
{
  "documentType": "yabt.packageManifest",
  "schemaVersion": 1,
  "sourcePath": "Photos/Vacation",
  "createdAtUtc": "2026-05-21T18:33:11.1234567Z",
  "format": "zip",
  "formatVersion": 1,
  "projectionId": "xxh128:AAAAAAAAAAAAAAAAAAAAAA",
  "packageName": "Vacation.xxh128-00000000000000000000000000.zip",
  "policy": {
    "format": "zip"
  },
  "entries": [
    {
      "kind": "file",
      "relativePath": "img001.jpg",
      "storedPath": "img001.jpg",
      "length": 4821031,
      "lastModifiedUtc": "2026-05-21T18:33:11.1234567Z",
      "contentHash": "xxh128:AAAAAAAAAAAAAAAAAAAAAA"
    }
  ],
  "totalBytes": 4821031,
  "manifestHash": "xxh128:AAAAAAAAAAAAAAAAAAAAAA"
}
```

The package's deterministic filename uses the full 128-bit projection identity encoded as lowercase unpadded Base32hex. JSON hashes use canonical unpadded Base64URL. Exact timestamps live in the manifest, so restore is not limited by the standard ZIP DOS timestamp's two-second resolution or its 1980 through 2107 year range. When an exact time lies outside that range, only the ZIP header is clamped; restore still uses the exact manifest value.

## Destination Logical-State Manifest

After a successful restore, YABT writes plain `.yabt-logical-state-manifest.json` at the filesystem destination root. Its document type is `yabt.logicalStateManifest` and its schema version is 1. It is destination comparison evidence, not an archive package manifest and not a cache with exclusive information.

Each canonical entry contains exactly:

- `logicalRelativePath`
- `statFingerprint`: the destination file's current canonical `stat-v1` length and UTC modification time.
- `contentHash`: the desired file's last validated actual-byte xxHash128 value.

The document has a canonical `manifestHash`. During mutation, `.yabt-logical-state-manifest.invalid` prevents interrupted work from leaving stale evidence that appears trustworthy. Restore publishes the rebuilt manifest only after the live filesystem is correct and removes the marker last.

On a normal later restore, YABT can avoid opening a destination file only when its current stat fingerprint matches the entry and the desired archive content hash matches the entry's hash. Missing, invalid, or invalidated evidence falls back to hashing the destination. `restore --byte-for-byte` bypasses the shortcut and reads every desired archive output through the bounded work pipeline. New or changed output is staged and hash-validated immediately before that file's blocking destination representation is historized and the staged file is atomically committed.

## History Manifest And References

The history manifest is `.yabt-history-manifest.json` under the configured history prefix. Its document type is `yabt.historyManifest`, and it records every non-control historical occurrence as either materialized or represented by a deduplication reference. Each entry contains its logical relative path, stored relative path, representation, content length, xxHash128 content hash, and optional observable last-modified time, content type, and provider metadata. A deterministic self-hash detects accidental changes to the manifest. `.yabt-history-manifest.invalid` marks the catalog as untrusted only during a mutation or after an interrupted one. When a successful backup changes history, it deletes the stale history manifest and then the marker; the next `deduplicate` run rebuilds the catalog. A successful `deduplicate` publishes the rebuilt document and removes its transaction marker last.

The history manifest is a rebuildable catalog, not an exclusive source of archive truth. A materialized entry contains only information YABT can recover by scanning or opening the actual object. A reference embeds its exact complete manifest entry. Losing the history manifest may require opening and hashing the materialized objects again, but must not make any historical object unidentifiable or unrestorable. Source-only change fingerprints and other unrebuildable values do not belong in this catalog.

A reference is versioned JSON with document type `yabt.historyContentReference`. Its filename is the complete original filename followed by `.yabt-ref.json`, such as `report.pdf.yabt-ref.json`. If that stored name exists, YABT uses a numeric fallback such as `report.pdf.1.yabt-ref.json` rather than overwriting it. The reference embeds its exact history entry and protects that entry with a deterministic self-hash. It identifies content by hash rather than pointing to another reference path. This prevents reference chains and allows the stable materialized backing object's location to be selected or recovered independently.

At least one stable materialized historical object must remain for every hash used by a reference. The current deduplication command scans only history. A future resolver could consider matching live objects as extra candidates, but a historical reference must remain restorable after those live objects change or disappear.

During MVP development, YABT applies these rules only to the exact current metadata paths. It does not search history for change manifests or invalidation markers placed there by older builds.
