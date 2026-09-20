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
- Optional default object store id.
- Optional change manifest compression.
- Optional history tiny-file threshold for deduplication.
- Non-secret store configuration or a reference to runtime configuration.
- Credential references for providers that support them.

It does not record operational state such as last backup time, scan cursors, upload checkpoints, cache keys, or retry state.

During MVP development, readers accept only `schemaVersion: 1`.

## Exact Archive Copy And Restore

`.yabt-root.json` is reserved root metadata, not an ordinary object in the logical live branch. A mutating backup validates the source document and copies its exact bytes to the selected archive root, outside `livePrefix` and outside every package. Exact means that whitespace, property order, line endings, and a UTF-8 byte-order mark are preserved. The schema-version-3 live change manifest records the archived descriptor's actual-byte xxHash128 value and length, so restore can detect a missing or changed copy before modifying the destination.

If an archived descriptor already exists with the same `archiveId` and layout but different bytes, backup moves the old document to history before replacing it. A different archive id or layout is refused because it would combine incompatible roots.

Backup dry-run and verify inspect this reserved object too. Dry-run reports a missing or byte-different copy as a pending exact installation, while verify is incomplete until the archived bytes match. Corrupt descriptors, incompatible identities or layouts, and disagreement with schema-version-3 descriptor evidence are refused.

Restore writes the archived descriptor bytes unchanged. In particular, YABT does not rewrite `rootRole`, store declarations, or machine-specific filesystem paths for the new destination. This is deliberate: after restore, inspect the file and manually update any path that is obsolete on the new machine.

For destination safety:

- A missing descriptor is installed.
- An exact byte match is left untouched.
- A different descriptor with the same archive id and layout is refused unless `--replace-root-descriptor` is supplied; with that option, the old document moves to history before replacement.
- A different archive id or layout is refused even with `--replace-root-descriptor`.

The archived layout is authoritative for restored live and history paths. The local source descriptor used to connect to the archive must identify the same archive id, but its runtime store configuration remains the way YABT locates that archive before it can read the preserved copy.

## Object Stores

Object stores are identified by provider-owned string names.

Initial store kinds:

- `fileSystem`
- `azureBlob`
- `webDav`

The same store can be a source, target, backup location, restore location, or reconciliation peer depending on the command being executed.

Provider-specific store parameters normally live in the same JSON object as the store declaration. For example, a filesystem store may have `rootPath`, while WebDAV may have `endpoint` and `rootPath`.

Azure Blob is configured through the optional `configSectionPath` property instead. An Azure store declaration contains only `id`, `kind`, and optionally `configSectionPath`. When the property is missing or null, YABT uses `ObjectStores:AzureBlob`.

Root validation checks every declared Azure store, not only the store selected for the current command. Any `credentialRef` or provider-specific property on an Azure declaration is rejected before backup can copy the descriptor, which prevents a secret-bearing unused declaration from leaking into another archive. Conversely, `configSectionPath` is rejected on non-Azure store declarations.

YABT binds the selected section from the application's merged `IConfiguration`. This allows non-secret values such as `ServiceUri`, `ContainerName`, `Prefix`, `UploadMaximumConcurrency`, and the nested `Retry` policy to come from `appsettings.json`, while credentials can come from User Secrets, environment variables, or another configuration provider. A nonempty `ConnectionString` takes precedence. Otherwise, YABT requires `ServiceUri` and uses the host's registered Azure `TokenCredential`, which defaults to `DefaultAzureCredential`. A host may register a more specific credential such as `ManagedIdentityCredential`. A token credential authenticates access but does not identify the storage account endpoint.

`UploadMaximumConcurrency` is a positive integer that limits the Azure SDK upload subtransfers that may run in parallel. It defaults to `5`. Set it lower for constrained or unstable network paths, or raise it deliberately after measuring transfer reliability and throughput.

The Azure client uses exponential retries for request failures and resumable streaming reads. `Retry:MaximumRetries` defaults to `8`, `Retry:Delay` to two seconds, `Retry:MaximumDelay` to 30 seconds, and `Retry:NetworkTimeout` to 100 seconds. Durations use normal .NET `TimeSpan` configuration strings. These retries are independent of `Sync:RestoreMaximumConcurrency`: the former controls recovery for one Azure request or stream, while the latter bounds the number of files being restored concurrently.

An Azure `ServiceUri` must be an absolute HTTPS URI and must not contain user information, a query, or a fragment. In particular, do not append a SAS token to this URI; deliver it through an explicitly supported runtime credential mechanism instead.

For a store that selects `ObjectStores:MainAzure`, non-secret `appsettings.json` values can look like this:

```json
{
  "ObjectStores": {
    "MainAzure": {
      "ServiceUri": "https://example.blob.core.windows.net",
      "ContainerName": "archive",
      "Prefix": "personal",
      "UploadMaximumConcurrency": 5,
      "Retry": {
        "MaximumRetries": 8,
        "Delay": "00:00:02",
        "MaximumDelay": "00:00:30",
        "NetworkTimeout": "00:01:40"
      }
    }
  }
}
```

Omit `ConnectionString` to use the registered token credential. If a connection string is required, supply `ObjectStores:MainAzure:ConnectionString` through User Secrets or the environment variable `ObjectStores__MainAzure__ConnectionString`, rather than committing it to an appsettings file.

When a command does not specify a target store id, the optional root-level `defaultStoreId` selects the default store. If neither the command nor the descriptor selects a store, commands use the first store declaration and warn when more than one store is configured.

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

If a real data name would clash with `.yabt-root.json`, any root change-manifest internal filename, `.yabt-policy.json`, `.yabt-tmp`, or the configured history prefix, initialize the root with alternate prefixes before using it.

When `livePrefix` is empty, YABT metadata paths and the configured history prefix are internal to the archive root. They are not ordinary live data even though they physically sit under the same root.

## Change Manifest Compression

The optional root-level `changeManifestCompression` property controls how YABT writes the root live change manifest:

- `brotli`: write `.yabt-change-manifest.json.br` using Brotli compression.
- `none`: write the uncompressed `.yabt-change-manifest.json` document.

When the property is omitted, YABT uses `brotli`. Readers inspect both supported file names regardless of the configured write format so that changing this option does not make an existing manifest unreadable. Values are case-sensitive, and no other compression names are accepted during MVP development.

## History Deduplication

The optional root-level `historyDeduplicationTinyFileMaximumBytes` property sets the largest historical object that the `deduplicate` command treats as tiny. Objects whose original content length is less than or equal to the effective threshold remain materialized instead of being replaced by a JSON reference. The value is a non-negative whole number of bytes and defaults to `4096` when omitted.

This option does not change synchronization behavior. History scanning and mandatory byte-for-byte duplicate confirmation occur only when the separate `deduplicate` command runs.

## Secrets

Secrets must not be stored in `.yabt-root.json`.

Store declarations may include a `credentialRef` value for providers such as WebDAV. Runtime configuration resolves that reference through mechanisms such as OS credential storage, environment variables, user secrets, or an external secret store.

Azure Blob declarations do not use `credentialRef` or contain endpoint, container, prefix, or authentication values. Their optional `configSectionPath` selects the runtime configuration section described above. When no explicit Azure credential is configured, `DefaultAzureCredential` can use a developer login locally or managed identity when YABT runs in Azure.

## Draft Shape

```json
{
  "documentType": "yabt.backupRoot",
  "schemaVersion": 1,
  "rootRole": "source",
  "archiveId": "018fc4c7-8ec8-7cf4-b5cb-5e31d5d8d15a",
  "name": "Personal archive",
  "defaultStoreId": "local-archive",
  "changeManifestCompression": "brotli",
  "historyDeduplicationTinyFileMaximumBytes": 4096,
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
      "configSectionPath": "ObjectStores:MainAzure"
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
