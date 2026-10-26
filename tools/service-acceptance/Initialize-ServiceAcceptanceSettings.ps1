#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This initializer prepares the Windows VM credential file.' }

$settingsUserProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([string]::IsNullOrWhiteSpace($settingsUserProfile)) { throw 'The user-profile directory is unavailable.' }
# AppData writes from an MSIX-hosted process can be redirected into its package.
$settingsDirectory = Join-Path $settingsUserProfile '.yabt/service-acceptance'
$settingsPath = Join-Path $settingsDirectory 'providers.local.json'
$templatePath = Join-Path $PSScriptRoot 'providers.example.json'

# Inspect path metadata only. Never read or replace an existing credential file.
foreach ($candidate in @((Join-Path $settingsUserProfile '.yabt'), $settingsDirectory, $settingsPath)) {
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Refusing a linked settings location.'
        }
    }
}
if (Test-Path -LiteralPath $settingsPath) {
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw 'The settings filename is occupied by a directory.'
    }
    Write-Host 'Existing service settings left unchanged.'
    Write-Output $settingsPath
    return
}

[IO.Directory]::CreateDirectory($settingsDirectory) | Out-Null
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($userSid)
$acl.SetAccessRuleProtection($true, $false)
$inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
foreach ($sid in @($userSid, $systemSid)) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow
    )
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $settingsDirectory -AclObject $acl

$templateBytes = [IO.File]::ReadAllBytes($templatePath)
$stream = [IO.FileStream]::new(
    $settingsPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None
)
try { $stream.Write($templateBytes) } finally { $stream.Dispose() }
Write-Host 'Created private service settings with empty credential fields.'
Write-Output $settingsPath
