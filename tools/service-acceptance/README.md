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

This slice prepares configuration only. The existing Linux solution runner and
CLI do not automatically load this file, and no remote requests are made by the
initializer. A deployed-service harness must load it explicitly, for example
through .NET's `AddJsonFile`, and bind the existing provider option sections.
Windows environment variables and User Secrets are not automatically available
inside the Linux container.

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
`CreateIfNotExistsAsync` even when the container already exists. Do not assume
a container-scoped SAS will pass that container PUT before testing it. If a SAS
is used, it belongs in a supported SAS connection string, never in `ServiceUri`
or `.yabt-root.json`. Connection strings take precedence over token credentials.

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

Provider-level tests can use these options directly. End-to-end CLI selection
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

The future Linux service runner should receive the private file through a
temporary read-only secret mount, without copying it into the persistent
workspace, source snapshot, or Docker image. Keep credentials out of container
configuration environment fields and command arguments. Load values inside the
test process and report only configuration presence and test outcomes. Keep raw
remote diagnostics private and redact before exporting: provider exceptions
can include server response bodies and request details.
