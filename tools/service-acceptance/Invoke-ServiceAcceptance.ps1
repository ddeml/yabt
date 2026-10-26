#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Linux')]
    [string]$Platform = 'Windows',
    [string]$SettingsPath = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.yabt/service-acceptance/providers.local.json')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Run this orchestration script from the Windows VM.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$settingsFile = Get-Item -LiteralPath $SettingsPath
if ($settingsFile.PSIsContainer -or ($settingsFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Use an ordinary private settings file.'
}
$SettingsPath = $settingsFile.FullName
$settingsAncestor = $settingsFile.Directory
while ($null -ne $settingsAncestor) {
    if (($settingsAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing a linked private settings ancestor.'
    }
    $settingsAncestor = $settingsAncestor.Parent
}
if ($SettingsPath.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Credentials and private results must be outside the repository.'
}
$dirty = & git -C $repositoryRoot status --porcelain=v1 --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit task changes before service acceptance.' }
$revision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve the source revision.' }
$runId = $revision.Substring(0, 12) + '-' + $Platform.ToLowerInvariant() + '-' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$privateRoot = Join-Path $settingsFile.DirectoryName "runs/$runId"
$publicRoot = Join-Path $repositoryRoot "artifacts/service-acceptance/$runId"
[IO.Directory]::CreateDirectory($privateRoot) | Out-Null
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$privateAcl = [Security.AccessControl.DirectorySecurity]::new()
$privateAcl.SetOwner($userSid)
$privateAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($userSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $privateAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $sid, [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow
    ))
}
Set-Acl -LiteralPath $privateRoot -AclObject $privateAcl
[IO.Directory]::CreateDirectory($publicRoot) | Out-Null
$issues = [Collections.Generic.List[string]]::new()
$projects = @('Yabt.AzureBlob.Tests', 'Yabt.WebDav.Tests', 'Yabt.Sync.Tests')

# Redirect every process stream to the private directory before starting it.
# Never echo commands, configuration values, remote exceptions, or raw TRX.
function Invoke-PrivateProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$Name,
        [hashtable]$Environment = @{}, [int]$TimeoutSeconds = 1200)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $output = [IO.File]::Create((Join-Path $privateRoot "$Name.stdout.log"))
    $errors = [IO.File]::Create((Join-Path $privateRoot "$Name.stderr.log"))
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($output)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync($errors)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $issues.Add("Process timed out: $Name")
        }
        $outputCopy.GetAwaiter().GetResult()
        $errorCopy.GetAwaiter().GetResult()
        return $process.ExitCode
    }
    finally { $process.Dispose(); $output.Dispose(); $errors.Dispose() }
}

function Invoke-PrivateDocker {
    param([string[]]$Arguments, [string]$Name, [int]$TimeoutSeconds = 1200)
    return Invoke-PrivateProcess 'docker' (@('--context', 'desktop-linux') + $Arguments) $Name @{} $TimeoutSeconds
}

$testResults = Join-Path $privateRoot 'tests'
if ($Platform -eq 'Windows') {
    $environment = @{
        YABT_SERVICE_ACCEPTANCE = '1'
        YABT_SERVICE_ACCEPTANCE_SETTINGS = $SettingsPath
        YABT_SERVICE_ACCEPTANCE_PRIVATE_RESULTS = (Join-Path $privateRoot 'diagnostics')
    }
    [IO.Directory]::CreateDirectory($environment.YABT_SERVICE_ACCEPTANCE_PRIVATE_RESULTS) | Out-Null
    foreach ($project in $projects) {
        Write-Output "Running $project deployed-service cases on Windows."
        $exitCode = Invoke-PrivateProcess 'dotnet' @(
            'test', "tests/$project/$project.csproj", '--configuration', 'Release',
            '--verbosity', 'minimal', '-m:1', '--disable-build-servers',
            '--filter', 'TestCategory=DeployedService', '--logger', 'trx',
            '--results-directory', (Join-Path $testResults $project)
        ) $project $environment
        if ($exitCode -ne 0) { $issues.Add("$project exited with code $exitCode.") }
    }
}
else {
    if ($SettingsPath.Contains(',')) { throw 'Docker bind-mount paths cannot contain commas.' }
    $containerName = 'yabt-service-' + $runId
    $archivePath = Join-Path $publicRoot 'source.tar'
    if ((Invoke-PrivateProcess 'git' @('archive', '--format=tar', '--output', $archivePath, $revision) 'archive') -ne 0) {
        throw 'Source export failed. See private logs.'
    }
    $checkout = "/work/checkouts/$runId"
    $containerArchive = "/work/imports/$runId.tar"
    try {
        $exitCode = Invoke-PrivateDocker @(
            'run', '-d', '--name', $containerName, '--init', '--user', '10001:10001',
            '--cpus', '2', '--memory', '3g', '--pids-limit', '512',
            '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
            '--mount', 'type=volume,source=yabt-linux-workspace,target=/work',
            '--mount', "type=bind,source=$SettingsPath,target=/run/secrets/providers.json,readonly",
            '--tmpfs', '/private-results:rw,nosuid,nodev,noexec,uid=10001,gid=10001,mode=0700,size=128m',
            'yabt-linux-acceptance:local', 'sleep', 'infinity'
        ) 'container-start'
        if ($exitCode -ne 0) { throw 'Cannot start the YABT service container. Start the documented Linux environment first.' }
        if ((Invoke-PrivateDocker @('cp', $archivePath, "${containerName}:$containerArchive") 'source-copy') -ne 0) {
            throw 'Cannot import the source snapshot.'
        }
        $import = 'set -euo pipefail; [[ ! -e "$2" && ! -L "$2" ]]; mkdir -- "$2"; tar --extract --file "$1" --directory "$2" --no-same-owner'
        if ((Invoke-PrivateDocker @('exec', $containerName, 'bash', '-c', $import, 'yabt-import', $containerArchive, $checkout) 'source-import') -ne 0) {
            throw 'Cannot unpack the source snapshot.'
        }
        Write-Output 'Running all deployed-service cases on Linux/ext4.'
        $exitCode = Invoke-PrivateDocker @(
            'exec', '--env', "YABT_SOURCE_REVISION=$revision", $containerName,
            'bash', "$checkout/tools/service-acceptance/run-service-acceptance.sh", $checkout, '/private-results'
        ) 'linux-tests' 3700
        if ($exitCode -ne 0) { $issues.Add("Linux tests exited with code $exitCode.") }
        $linuxResults = Join-Path $privateRoot 'linux'
        [IO.Directory]::CreateDirectory($linuxResults) | Out-Null
        if ((Invoke-PrivateDocker @('cp', "${containerName}:/private-results/.", $linuxResults) 'results-copy') -ne 0) {
            $issues.Add('Private Linux results could not be exported.')
        }
        $testResults = Join-Path $linuxResults 'tests'
        if (-not (Test-Path -LiteralPath (Join-Path $linuxResults 'run-status.txt'))) {
            $issues.Add('The Linux runner did not complete all project runs.')
        }
    }
    catch { $issues.Add($_.Exception.Message) }
    finally {
        if ((Invoke-PrivateDocker @('rm', '-f', $containerName) 'container-remove') -ne 0) {
            $issues.Add('Temporary service container removal failed; inspect it before continuing.')
        }
    }
}

$required = @{
    'Yabt.AzureBlob.Tests.AzureBlobDeployedServiceTests.ProviderContract' = 6
    'Yabt.AzureBlob.Tests.AzureBlobDeployedServiceTests.ExactObjectAndSameNamedPrefixRemainDistinct' = 1
    'Yabt.WebDav.Tests.WebDavDeployedServiceTests.ProviderContract' = 6
    'Yabt.WebDav.Tests.WebDavDeployedServiceTests.WholeFolderMovePreservesNativeEmptyCollectionsAndRejectsCollisions' = 1
    'Yabt.Sync.Tests.DeployedServiceSynchronizationTests.BackupRestorePreservesContentAndHistory' = 4
}
$rows = [Collections.Generic.List[object]]::new()
$rowKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($project in $projects) {
    $reports = @(Get-ChildItem -LiteralPath (Join-Path $testResults $project) -Filter '*.trx' -ErrorAction SilentlyContinue)
    if ($reports.Count -ne 1) { $issues.Add("Expected exactly one TRX for $project."); continue }
    try {
        [xml]$document = [IO.File]::ReadAllText($reports[0].FullName)
        $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        $definitions = @{}
        foreach ($definition in $document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns)) {
            $method = $definition.SelectSingleNode('t:TestMethod', $ns)
            $definitions[$definition.GetAttribute('id')] = $method.GetAttribute('className').Split(',')[0] + '.' + $method.GetAttribute('name')
        }
        foreach ($result in $document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns)) {
            $method = $definitions[$result.GetAttribute('testId')]
            if (-not $method -or -not $required.ContainsKey($method)) { throw 'Unexpected test row.' }
            if (-not $rowKeys.Add($method + ':' + $result.GetAttribute('testName'))) {
                throw 'Duplicate test row.'
            }
            $rows.Add([ordered]@{ project = $project; method = $method;
                testName = $result.GetAttribute('testName'); outcome = $result.GetAttribute('outcome') })
        }
    }
    catch { $issues.Add("Cannot validate TRX for $project; raw details retained privately.") }
}
foreach ($entry in $required.GetEnumerator()) {
    $matching = @($rows | Where-Object { $_.method -ceq $entry.Key })
    if ($matching.Count -ne $entry.Value -or @($matching | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0) {
        $issues.Add("Required cases did not all pass: $($entry.Key)")
    }
}
$summary = [ordered]@{
    schemaVersion = 1; sourceRevision = $revision; platform = $Platform
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    accepted = $issues.Count -eq 0
    counts = @{ total = $rows.Count; passed = @($rows | Where-Object { $_.outcome -ceq 'Passed' }).Count }
    cases = $rows.ToArray(); validationIssues = $issues.ToArray()
}
$summaryPath = Join-Path $publicRoot 'acceptance-summary.json'
[IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output "Service acceptance summary: $summaryPath"
Write-Output "Private diagnostics: $privateRoot"
Write-Output "Passed $($summary.counts.passed) of $($summary.counts.total) observed cases; expected 18."
if (-not $summary.accepted) { throw 'Deployed-service acceptance did not pass. Inspect the summary and private diagnostics.' }
