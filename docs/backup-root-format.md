# Backup Root Format

The archive root descriptor identifies an archive tree and the object stores it can use.

The draft file name is:

```text
.backup-root.json
```

This file belongs at the root of a source or archive tree. It should be human-readable and safe to copy, commit, inspect, and replicate.

## Purpose

The root descriptor records:

- Archive identity.
- Archive metadata format version.
- Optional root role, such as `source` or `target`.
- Layout prefixes such as `live` and `hist`.
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
- `target`: an archive root that is intended to contain `live` and `hist`.

The role is advisory. Commands still decide operation direction, and object stores remain symmetrical.

## Secrets

Secrets must not be stored in `.backup-root.json`.

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
    "livePrefix": "live",
    "histPrefix": "hist"
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
