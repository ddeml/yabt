# Deployed-service acceptance credentials

Keep the editable credentials outside the repository. From PowerShell on the
Windows VM, run:

```powershell
./tools/service-acceptance/Initialize-ServiceAcceptanceSettings.ps1
```

The initializer copies `providers.example.json` to:

```text
%USERPROFILE%\.yabt\service-acceptance\providers.local.json
```

This location is outside AppData because Windows redirected the earlier
AppData path into Codex's private MSIX package storage. Commands launched by
Codex could see that file while ordinary Notepad and Explorer could not. The
user-profile path gives both processes the same file location. The initializer
does not migrate or overwrite a file from the earlier location automatically.

When creating a new file, it restricts the directory to the current Windows
user and SYSTEM. It refuses linked settings locations and leaves an existing
file and its permissions untouched. The JSON is plain text protected by
filesystem permissions; it is not an encrypted vault.
Edit only that private copy, then tell the agent that it is ready. Do not paste
credentials into chat, shell command arguments, or a tracked repository file.

The initializer never overwrites an existing file. Reopen the private file from
disk before editing it if another process has updated it; saving a stale editor
buffer can overwrite recently configured credentials. The acceptance tests read
the file without modifying it.

The dedicated runner below explicitly loads these settings. The ordinary Linux
solution runner and CLI do not load this file. Windows environment variables and
User Secrets are not automatically available inside the Linux container.

## Run deployed-service acceptance

Commit the tested changes first, then run from this repository on the VM:

```powershell
./tools/service-acceptance/Invoke-ServiceAcceptance.ps1 -Platform Windows
./tools/service-acceptance/Invoke-ServiceAcceptance.ps1 -Platform Linux
```

Use `-Provider AzureBlob` or `-Provider WebDav` to retry just one service.
Each selected service requires all nine of its cases to pass; the summary
records the selection, so a partial run cannot stand for acceptance of both.

The Linux invocation requires the existing `yabt-linux` environment to have
been started using `tools/linux-acceptance/Invoke-LinuxAcceptance.ps1 -Action
Start`. It creates a temporary companion container using the same SDK image and
Linux/ext4 volume. Only the private settings file is mounted from Windows, read
only at `/run/secrets/providers.json`. Credentials are never placed in Docker
environment fields, command arguments, source snapshots, or the image. The
companion container is removed after the run; other YABT and DSA containers are
not changed.

The suite contains 18 cases: six shared provider contracts and one additional
provider-specific case for each service, plus mirror and ZIP synchronization
round trips against each service. It covers immediate traversal and exact names,
create-if-absent, conditional replacement/deletion, complete-folder moves,
failed uploads, lock contention/reacquisition, and provider-specific namespace
behavior. Synchronization cases cover initial and unchanged backup, changed and
deleted source files, history preservation, byte verification, and restore with
exact content, timestamps, and empty directories. These use the synchronization
API; they do not claim production CLI provider-resolution coverage, long-running
lock renewal, abrupt-process cloud recovery, or performance acceptance.

Each case creates a unique child below the configured Azure prefix or WebDAV
root. Azure requires the selected container to exist. Successful cases remove
only their owned child subtree and verify its removal. Failed cases retain their
remote fixture for diagnosis. No test deletes a selected container or the
configured base folder, and preexisting data is not cleaned up.

Ordinary test runs skip these cases. Direct execution requires both
`YABT_SERVICE_ACCEPTANCE=1` and an explicit
`YABT_SERVICE_ACCEPTANCE_SETTINGS` file path. Use the runner for the normal
workflow: it captures stdout, stderr, TRX, and optional raw service exceptions
under the private settings directory's `runs` folder. On Linux, raw results
stay in a private tmpfs until exported there. Treat these files as sensitive;
never paste or commit them without redaction.

The public `artifacts/service-acceptance/<run>/acceptance-summary.json` contains
the source commit, platform, test names, and outcomes only. Acceptance requires
all 18 expected cases to pass; skipped, missing, failed, or interrupted cases do
not count as successful acceptance. Individual projects have a 20-minute
watchdog. Build/test logs are intentionally absent from terminal output.

## Azure Blob Storage

For the simplest connection-string setup, fill
`ObjectStores.AzureBlob.ConnectionString` with a connection string for a
dedicated disposable test storage account. Set `ContainerName` to the intended
test container; leave `ServiceUri` null. The suggested `Prefix` keeps YABT's test
data identifiable. It is an application convention, not an authorization
boundary. An account-key connection string grants access beyond one container,
so do not use a production account key merely because the test container is
separate.

Alternatively, use Microsoft Entra authentication: leave `ConnectionString`
empty, set `ServiceUri` to the account's clean HTTPS Blob endpoint, and provide
`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `AZURE_CLIENT_SECRET` to the test
process environment. A dedicated service principal with **Storage Blob Data
Contributor** on the existing test container is a suitable starting scope.
The registered `DefaultAzureCredential` consumes those environment variables;
adding identically named properties to arbitrary JSON does not configure it.
See Microsoft's
[service-principal guidance](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication-local-development-service-principal)
and
[Storage Blob Data Contributor](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage#storage-blob-data-contributor).

The acceptance operations need container readiness/initialization and blob
read, list, create/write, and delete access. YABT's archive lock uses conditional
blob writes and deletes, not Azure native blob leases. Initialization calls
`CreateIfNotExistsAsync` even when the container already exists. Readiness also
reads container properties. A container-scoped service SAS does not authorize
these container operations; use a supported identity or account credential.
If a SAS is used, it belongs in a supported SAS connection string, never in
`ServiceUri` or `.yabt-root.json`. Connection strings take precedence over token
credentials. See Microsoft's
[container-properties authorization rules](https://learn.microsoft.com/en-us/rest/api/storageservices/get-container-properties)
and
[container-creation authorization rules](https://learn.microsoft.com/en-us/rest/api/storageservices/create-container).

## pCloud WebDAV

Fill `ObjectStores.WebDav` as follows:

| Field | Value |
| --- | --- |
| `Endpoint` | `https://ewebdav.pcloud.com` for EU data |
| `Endpoint` | `https://webdav.pcloud.com` for US data |
| `UserName` | The pCloud account email address |
| `Password` | The pCloud account password |
| `RootPath` | A dedicated ordinary folder for disposable YABT test data |

Keep the endpoint free of credentials and query parameters. The suggested
`RootPath` is `yabt-acceptance/codexsandbox01`. It must not select your normal
backup folder. Future test runs should create unique children beneath this
location and remove only their own fixtures. The account password itself is not
restricted to that folder.

The installed pCloud desktop session does not configure YABT's HTTP Basic
authentication. pCloud's current instructions say to approve the email login
notification when 2FA requires it; disabling 2FA is unnecessary. Its Crypto
Folder is unavailable through WebDAV. See
[pCloud's connection instructions](https://help.pcloud.com/article/connect-to-pcloud-using-webdav-and-rsync).

Provider and synchronization tests use these options directly. CLI selection
still needs a WebDAV root-store resolver; supplying credentials alone does not
implement that missing integration. `CredentialRef` is not implemented and
does not retrieve the desktop application's credentials.

## Environment variables and test execution

The standard configuration equivalents use double underscores, for example:

```text
ObjectStores__AzureBlob__ConnectionString
ObjectStores__AzureBlob__ServiceUri
ObjectStores__AzureBlob__ContainerName
ObjectStores__AzureBlob__Prefix
ObjectStores__WebDav__Endpoint
ObjectStores__WebDav__RootPath
ObjectStores__WebDav__UserName
ObjectStores__WebDav__Password
```

Prefer the private file for repeatable VM testing. A variable set in another
terminal does not update an already-running agent or container. If variables
are preferred, inject them into the actual test process rather than assuming
machine/user settings have refreshed every process.

The Linux service runner receives the private file through a temporary read-only
mount, without copying it into the persistent workspace, source snapshot, or
Docker image. Keep credentials out of container configuration environment fields
and command arguments. Keep raw remote diagnostics private and redact before
exporting: provider exceptions can include server response bodies and request
details.
