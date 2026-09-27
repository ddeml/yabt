# Folder Policy Format

Folder policy files define how a logical folder is projected by an archive
format. They are not operational state.

Policy files are named:

```text
.yabt-policy.json
```

In a source tree, the policy file lives inside the folder it describes, so it
moves naturally when a folder is reorganized.

The exact policy file travels with the folder representation and returns on
restore. In `mirror` it remains an ordinary visible file. In `zip` it is an
ordinary package payload entry, while the visible adjacent package manifest
contains the parsed canonical `policy` snapshot needed to identify and inspect
that projection without opening the ZIP. The packaged source folder does not
become a target folder of its own.

## Format

The `format` property selects the archive format handler. Format names are
provider-owned strings, not C# enum values.

Initial formats:

- `mirror`: store files individually.
- `zip`: store the folder as a zip package artifact.

There is no `auto` format initially.

When no policy file exists, the default format is `mirror`.

The selected format describes the intended representation of the source folder.
It does not own target comparison, historization, or delete handling. The
synchronizer applies the projected representation to the configured archive
layout and preserves replaced or deleted target objects under the configured
history prefix. For ZIP, the canonical policy snapshot is part of the
deterministic projection identity, so a policy change creates a new package
representation even when payload bytes are unchanged.

## Provider Options

Common policy fields stay at the top level. Provider-specific configuration
belongs under `options` .

`includePatterns` , `excludePatterns` , and `options` are optional. Omit them
when they are empty or not needed.

The common schema validates the policy shape. A format handler may supply
stricter validation for its own `options` object.

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

## Directory Links

The optional `directoryLinks` map authorizes source traversal through explicitly
named immediate child directory links. Each key preserves the child's exact
spelling and names one directory, without `/` , `\` , `.` or `..` path
components. An entry requires both `behavior: "follow"` and a nonempty
`expectedTarget` :

```json
{
  "format": "mirror",
  "directoryLinks": {
    "Photos": {
      "behavior": "follow",
      "expectedTarget": "D:\\PhotoArchive"
    }
  }
}
```

The filesystem source checks that the link resolves to the expected target
before following it. Relative expected targets are interpreted relative to the
physical folder containing the policy, including when that folder was reached
through another approved link. This permission applies only to source traversal;
it does not authorize following links in archive targets, history, staging, or
restore destinations. Restore materializes the archived contents as ordinary
directories rather than recreating links.

Unapproved, mismatched, or unresolved links fail the operation rather than
disappearing from its input. Directory identity checks reject ancestor cycles
even across nested ZIP projections. Repeated noncyclic aliases in separate
branches remain separate logical archive content. Approval cannot override
overlap with the selected filesystem archive, source history, or source staging.
There is no CLI bypass.

The initial implementation supports local Windows directory identities and Linux
`statx` inode/mount identities (Linux 5.8 or later). It refuses approved-link
traversal involving Windows UNC storage because share aliases can hide
overlapping directories. On Linux it refuses different mounts of the same device
relative to protected storage, and ordinary mount transitions beneath an
approved link. Unsupported identity APIs also cause refusal. These restrictions
apply to approved descendant links; ordinary source trees do not require the
native identity APIs.

Directory and link topology must remain unchanged by other programs during the
operation. Revalidation detects changes between checks, but path-based
filesystem calls are not an adversarial sandbox against concurrent link
replacement.

Unknown link-entry properties, duplicate map keys, null entries, unsupported
behaviors, and empty expected targets are rejected. An absent, null, or empty
map grants no traversal permission. Target strings remain unchanged in archived
policy snapshots and are not interpreted on the restore host. ZIP manifests
order link names ordinally and include the map in their self-hash and package
identity when nonempty. Policies without this field retain their previous
canonical bytes and identity.

## Operational State

Do not store scan cursors, upload checkpoints, cache keys, last successful
backup times, or retry state in folder policy files.
