# Folder Policy Format

Folder policy files define how a logical folder is projected by an archive format. They are not operational state.

Policy files are named:

```text
.yabt-policy.json
```

In a source tree, the policy file lives inside the folder it describes, so it moves naturally when a folder is reorganized.

The exact policy file travels with the folder representation and returns on restore. In `mirror` it remains an ordinary visible file. In `zip` it is an ordinary package payload entry, while the visible adjacent package manifest contains the parsed canonical `policy` snapshot needed to identify and inspect that projection without opening the ZIP. The packaged source folder does not become a target folder of its own.

## Format

The `format` property selects the archive format handler. Format names are provider-owned strings, not C# enum values.

Initial formats:

- `mirror`: store files individually.
- `zip`: store the folder as a zip package artifact.

There is no `auto` format initially.

When no policy file exists, the default format is `mirror`.

The selected format describes the intended representation of the source folder. It does not own target comparison, historization, or delete handling. The synchronizer applies the projected representation to the configured archive layout and preserves replaced or deleted target objects under the configured history prefix. For ZIP, the canonical policy snapshot is part of the deterministic projection identity, so a policy change creates a new package representation even when payload bytes are unchanged.

## Provider Options

Common policy fields stay at the top level. Provider-specific configuration belongs under `options`.

`includePatterns`, `excludePatterns`, and `options` are optional. Omit them when they are empty or not needed.

The common schema validates the policy shape. A format handler may supply stricter validation for its own `options` object.

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

Do not store scan cursors, upload checkpoints, cache keys, last successful backup times, or retry state in folder policy files.
