# Folder Policy Format

Folder policy files define how a logical folder is projected by an archive format. They are not operational state.

Policy files are named:

```text
.yabt-policy.json
```

In a source tree, the policy file lives inside the folder it describes, so it moves naturally when a folder is reorganized.

When a folder is packaged in an archive target, a copy of the folder policy or equivalent folder descriptor should remain visible outside the package artifact, near the package and adjacent manifest.

## Format

The `format` property selects the archive format projector. Format names are provider-owned strings, not C# enum values.

Initial formats:

- `mirror`: store files individually.
- `zip`: store the folder as a zip package artifact.

There is no `auto` format initially.

When no policy file exists, the default format is `mirror`.

The selected format describes the intended representation of the source folder. It does not own target comparison, historization, or delete handling. The synchronizer applies the projected representation to the configured archive layout and preserves replaced or deleted target objects under the configured history prefix.

## Provider Options

Common policy fields stay at the top level. Provider-specific configuration belongs under `options`.

`includePatterns`, `excludePatterns`, and `options` are optional. Omit them when they are empty or not needed.

The common schema validates the policy shape. A format projector may supply stricter validation for its own `options` object.

## Draft Shape

A minimal mirror policy is:

```json
{
  "format": "mirror"
}
```

A zip policy with provider options is:

```json
{
  "format": "zip",
  "includePatterns": [
    "**/*"
  ],
  "excludePatterns": [
    "**/Thumbs.db",
    "**/.DS_Store"
  ],
  "options": {
    "compressionLevel": 6
  }
}
```

## Operational State

Do not store scan cursors, upload checkpoints, cache keys, last successful sync times, or retry state in folder policy files.
