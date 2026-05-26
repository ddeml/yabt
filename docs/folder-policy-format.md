# Folder Policy Format

Folder policy files define intent. They are not operational state.

Policy files are named:

```text
.backup-policy.json
```

They live inside the folder they describe, so they move naturally when a folder is reorganized.

## Modes

- `mirror`: upload files individually.
- `package`: create a standard archive before upload.
- `auto`: allow the tool to choose a mode later.

## Draft Shape

```json
{
  "mode": "package",
  "format": "sevenZip",
  "includePatterns": [
    "**/*"
  ],
  "excludePatterns": [
    "**/Thumbs.db",
    "**/.DS_Store"
  ]
}
```

When no policy file exists, the default is `mirror`.

## Operational State

Do not store scan cursors, upload checkpoints, or cache keys in folder policy files. Those values belong in a disposable cache and must be rebuildable.
