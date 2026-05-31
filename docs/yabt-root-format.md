# YABT Root Format

The YABT root descriptor identifies a source or archive tree and the object stores it can use.

The draft file name is:

```text
.yabt-root.json
```

This file belongs at the root of a source or archive tree. It should be human-readable and safe to copy, commit, inspect, and replicate.

## Purpose

The root descriptor records:

- Archive identity.
- Archive metadata format version.
- Optional root role, such as `source` or `target`.
- Layout prefixes for logical live and history branches.
- Known object stores.
- Non-secret store configuration.
- Credential references.

It does not record operational state such as last sync time, scan cursors, upload checkpoints, cache keys, or retry state.

## Object Stores

Object stores are identified by provider-owned string names.

Initial store kinds:

- `fileSystem`
- `azureBlob`
- `webDav`

The same store can be a source, target, backup location, restore location, or reconciliation peer depending on the command being executed.

Provider-specific store parameters live in the same JSON object as the store declaration. For example, a filesystem store may have `rootPath`, Azure Blob may have `accountUri`, `container`, and `prefix`, and WebDAV may have `endpoint` and `rootPath`.

## Root Role

The optional `rootRole` property indicates the default intended role of the root containing this descriptor:

- `source`: an ordinary folder tree that is intended to be backed up.
- `target`: an archive root that is intended to receive synchronized archive data.

The role is advisory. It does not imply a physical layout. Commands still decide operation direction, and object stores remain symmetrical.

## Layout

The `layout` object maps logical archive branches to physical object prefixes:

- `livePrefix`: current logical state.
- `histPrefix`: obsolete, replaced, or deleted historical state.

The default root layout is:

```json
{
  "livePrefix": "",
  "histPrefix": ".yabt-hist"
}
```

An empty `livePrefix` means the logical live branch is rooted at the actual object-store root. This is the normal layout for ordinary source folders and avoids forcing real data below a `live` child folder.

Archive-style roots may instead use explicit branch directories:

```json
{
  "livePrefix": "live",
  "histPrefix": "hist"
}
```

If a real data name would clash with `.yabt-root.json`, `.yabt-policy.json`, or the configured history prefix, initialize the root with alternate prefixes before using it.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root. They are not ordinary live data even though they physically sit under the same root.

## Secrets

Secrets must not be stored in `.yabt-root.json`.

Store declarations may include a `credentialRef` value. Runtime configuration resolves that reference through mechanisms such as Azure identity, OS credential storage, environment variables, user secrets, or an external secret store.

## Draft Shape

```json
{
  "documentType": "yabt.backupRoot",
  "schemaVersion": 1,
  "rootRole": "source",
  "archiveId": "018fc4c7-8ec8-7cf4-b5cb-5e31d5d8d15a",
  "name": "Personal archive",
  "createdAtUtc": "2026-05-28T18:30:00Z",
  "layout": {
    "livePrefix": "",
    "histPrefix": ".yabt-hist"
  },
  "stores": [
    {
      "id": "local-archive",
      "kind": "fileSystem",
      "rootPath": "E:\\Archive"
    },
    {
      "id": "main-azure",
      "kind": "azureBlob",
      "accountUri": "https://example.blob.core.windows.net",
      "container": "archive",
      "prefix": "personal",
      "credentialRef": "main-azure"
    },
    {
      "id": "pcloud",
      "kind": "webDav",
      "endpoint": "https://webdav.pcloud.com",
      "rootPath": "/YABT",
      "credentialRef": "pcloud-main"
    }
  ]
}
```
