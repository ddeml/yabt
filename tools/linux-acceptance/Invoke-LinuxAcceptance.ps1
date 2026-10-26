#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Test', 'Status', 'Stop')]
    [string] $Action = 'Test',
    [string] $Revision = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dockerArguments = @('--context', 'desktop-linux')
$composeArguments = $dockerArguments + @(
    'compose', '--project-name', 'yabt-linux', '--file',
    (Join-Path $PSScriptRoot 'compose.yaml')
)

function Invoke-CheckedCommand {
    param([string] $Command, [string[]] $Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command exited with code $LASTEXITCODE." }
}

function Get-CommandOutput {
    param([string] $Command, [string[]] $Arguments)
    $commandLines = @(Invoke-CheckedCommand $Command $Arguments)
    return ($commandLines -join "`n").Trim()
}

function Write-JsonFile {
    param([string] $Path, [object] $Value)
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-AcceptanceSummary {
    param(
        [string] $ResultDirectory,
        [string] $Commit,
        [int] $RunnerExitCode,
        [int] $CopyExitCode
    )
    $expectedProjects = @(
        'Yabt.Common.Tests', 'Yabt.Core.Tests', 'Yabt.Metadata.Tests',
        'Yabt.FileSystem.Tests', 'Yabt.Format.Mirror.Tests', 'Yabt.Format.Zip.Tests',
        'Yabt.Sync.Tests', 'Yabt.AzureBlob.Tests', 'Yabt.WebDav.Tests', 'Yabt.Cli.Tests'
    )
    $issues = [Collections.Generic.List[string]]::new()
    $rows = [Collections.Generic.List[object]]::new()
    $projectReports = @{}
    $methodRows = @{}
    $reports = @(Get-ChildItem -LiteralPath $ResultDirectory -Filter '*.trx' -Recurse)
    foreach ($report in $reports) {
        try {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($report.FullName, $settings)
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            try { $document.Load($reader) } finally { $reader.Dispose() }
            $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
            $namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
            $definitions = @{}
            $reportProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($definition in $document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $namespaces)) {
                $method = $definition.SelectSingleNode('t:TestMethod', $namespaces)
                if ($null -eq $method) { throw 'A TRX test definition lacks its method.' }
                $project = [IO.Path]::GetFileNameWithoutExtension($method.GetAttribute('codeBase').Replace('\', '/'))
                $definitionValue = @{
                    project = $project
                    className = $method.GetAttribute('className')
                    methodName = $method.GetAttribute('name')
                }
                $definitions[$definition.GetAttribute('id')] = $definitionValue
                $reportProjects.Add($project) | Out-Null
            }
            if ($reportProjects.Count -ne 1) { throw 'Each TRX must identify exactly one test project.' }
            $projectName = @($reportProjects)[0]
            if ($projectReports.ContainsKey($projectName)) { throw "Duplicate project report: $projectName" }
            $projectReports[$projectName] = $report.Name
            $summaryNode = $document.SelectSingleNode('/t:TestRun/t:ResultSummary', $namespaces)
            if ($null -eq $summaryNode -or $summaryNode.GetAttribute('outcome') -notin @('Completed', 'Passed')) {
                $issues.Add("$($report.Name): test run did not complete successfully.")
            }
            $resultNodes = $document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespaces)
            if ($resultNodes.Count -eq 0) { throw 'The TRX contains no test results.' }
            $counters = $document.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $namespaces)
            if ($null -eq $counters -or [int]$counters.GetAttribute('total') -ne $resultNodes.Count) {
                throw 'The TRX total does not match its result rows.'
            }
            foreach ($result in $resultNodes) {
                $testId = $result.GetAttribute('testId')
                if (-not $definitions.ContainsKey($testId)) { throw 'A TRX result lacks its test definition.' }
                $definitionValue = $definitions[$testId]
                $message = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespaces)
                $row = [pscustomobject]@{
                    project = $definitionValue.project
                    className = $definitionValue.className
                    methodName = $definitionValue.methodName
                    testName = $result.GetAttribute('testName')
                    outcome = $result.GetAttribute('outcome')
                    reason = if ($null -eq $message) { $null } else { $message.InnerText }
                }
                $rows.Add($row)
                $methodKey = $row.className + '.' + $row.methodName
                if (-not $methodRows.ContainsKey($methodKey)) {
                    $methodRows[$methodKey] = [Collections.Generic.List[object]]::new()
                }
                $methodRows[$methodKey].Add($row)
            }
        }
        catch { $issues.Add("$($report.Name): $($_.Exception.Message)") }
    }
    foreach ($project in $expectedProjects) {
        if (-not $projectReports.ContainsKey($project)) { $issues.Add("Missing project report: $project") }
    }
    foreach ($project in $projectReports.Keys) {
        if ($project -notin $expectedProjects) { $issues.Add("Unexpected project report: $project") }
    }

    $required = [Collections.Generic.List[object]]::new()
    $requirementsPath = Join-Path $ResultDirectory 'required-test-cases.tsv'
    try {
        if (-not (Test-Path -LiteralPath $requirementsPath)) {
            throw 'The required-test-cases.tsv acceptance inventory is missing.'
        }
        $requirements = @(Import-Csv -LiteralPath $requirementsPath -Delimiter "`t")
        $requiredTotal = 0
        $requiredKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($requirement in $requirements) {
            $methodKey = $requirement.className + '.' + $requirement.methodName
            $expected = [int]::Parse($requirement.passedRows, [Globalization.CultureInfo]::InvariantCulture)
            $requiredTotal += $expected
            if (-not $requiredKeys.Add($methodKey) -or $expected -lt 1) {
                $issues.Add("Invalid or duplicate required method: $methodKey")
            }
            $matching = @()
            if ($methodRows.ContainsKey($methodKey)) { $matching = @($methodRows[$methodKey].ToArray()) }
            $passedRows = @($matching | Where-Object outcome -eq 'Passed').Count
            $satisfied = $matching.Count -eq $expected -and $passedRows -eq $expected
            $required.Add([pscustomobject]@{
                method = $methodKey
                expectedPassedRows = $expected
                actualRows = $matching.Count
                passedRows = $passedRows
                satisfied = $satisfied
            })
            if (-not $satisfied) { $issues.Add("Required rows did not all pass: $methodKey") }
        }
        if ($requirements.Count -ne 6 -or $requiredTotal -ne 13) {
            $issues.Add('The acceptance inventory must contain six required methods and thirteen passing rows.')
        }
    }
    catch { $issues.Add($_.Exception.Message) }

    $skips = @($rows | Where-Object outcome -In @('NotExecuted', 'Inconclusive'))
    $failures = @($rows | Where-Object outcome -NotIn @('Passed', 'NotExecuted', 'Inconclusive'))
    if ($RunnerExitCode -ne 0) { $issues.Add("The Linux runner exited with code $RunnerExitCode.") }
    if ($CopyExitCode -ne 0) { $issues.Add("Result export exited with code $CopyExitCode.") }
    $runnerStatus = Join-Path $ResultDirectory 'run-status.txt'
    if (-not (Test-Path -LiteralPath $runnerStatus) -or
        -not (Select-String -LiteralPath $runnerStatus -Pattern '^phase=complete$' -Quiet)) {
        $issues.Add('The Linux runner did not record completed execution.')
    }
    return [ordered]@{
        schemaVersion = 1
        sourceRevision = $Commit
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runnerExitCode = $RunnerExitCode
        copyExitCode = $CopyExitCode
        accepted = $issues.Count -eq 0 -and $failures.Count -eq 0
        counts = @{ total = $rows.Count; passed = @($rows | Where-Object outcome -eq 'Passed').Count; skipped = $skips.Count; failed = $failures.Count }
        projects = @($projectReports.Keys | Sort-Object)
        requiredCases = $required.ToArray()
        skips = $skips
        failures = $failures
        validationIssues = $issues.ToArray()
    }
}

$engine = Get-CommandOutput docker ($dockerArguments + @('info', '--format', '{{.OSType}}'))
if ($engine -ne 'linux') { throw 'The desktop-linux context must use a Linux engine.' }
if ($Action -eq 'Status') {
    Invoke-CheckedCommand docker ($composeArguments + @('ps', '--all'))
    return
}
if ($Action -eq 'Stop') {
    Invoke-CheckedCommand docker ($composeArguments + @('stop', '--timeout', '20'))
    return
}

$commit = $null
if ($Action -eq 'Test') {
    $dirty = Get-CommandOutput git @('-C', $repositoryRoot, 'status', '--porcelain=v1', '--untracked-files=normal')
    if ($dirty.Length -ne 0) { throw 'Commit task changes before importing a Linux acceptance snapshot.' }
    $commit = Get-CommandOutput git @('-C', $repositoryRoot, 'rev-parse', '--verify', '--end-of-options', "$Revision^{commit}")
    if ($commit -notmatch '^[0-9a-f]{40}$') { throw 'The requested revision did not resolve to a commit.' }
}
Invoke-CheckedCommand docker ($composeArguments + @('build', 'acceptance'))
Invoke-CheckedCommand docker ($composeArguments + @('up', '-d', '--no-build'))
if ($Action -eq 'Start') { return }

$runId = $commit.Substring(0, 12) + '-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$resultDirectory = Join-Path $repositoryRoot "artifacts/linux-acceptance/$runId"
if (Test-Path -LiteralPath $resultDirectory) { throw 'The unique result directory already exists.' }
[IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
$archivePath = Join-Path $resultDirectory 'source.tar'
Invoke-CheckedCommand git @('-C', $repositoryRoot, 'archive', '--format=tar', '--output', $archivePath, $commit)
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$containerId = Get-CommandOutput docker ($composeArguments + @('ps', '-q', 'acceptance'))
if ($containerId -notmatch '^[0-9a-f]{12,64}$') { throw 'Exactly one acceptance container must be running.' }
$inspectTemplate = '{"id":{{json .Id}},"name":{{json .Name}},"imageId":{{json .Image}},"imageReference":{{json .Config.Image}},"user":{{json .Config.User}},"nanoCpus":{{json .HostConfig.NanoCpus}},"memoryBytes":{{json .HostConfig.Memory}},"pidsLimit":{{json .HostConfig.PidsLimit}},"mounts":{{json .Mounts}}}'
$containerMetadata = Get-CommandOutput docker ($dockerArguments + @('inspect', '--format', $inspectTemplate, $containerId)) | ConvertFrom-Json
Write-JsonFile (Join-Path $resultDirectory 'container-metadata.json') @{
    sourceRevision = $commit
    sourceArchiveSha256 = $archiveHash
    dockerContext = 'desktop-linux'
    container = $containerMetadata
}
$containerArchive = "/work/imports/$runId.tar"
$containerCheckout = "/work/checkouts/$runId"
$containerResults = "/work/runs/$runId"
Invoke-CheckedCommand docker ($dockerArguments + @('cp', $archivePath, "${containerId}:$containerArchive"))
$importScript = @'
set -euo pipefail
snapshot_archive="$1"
snapshot_checkout="$2"
snapshot_hash="$3"
read -r actual_hash archive_name < <(sha256sum -- "$snapshot_archive")
[[ "$actual_hash" == "$snapshot_hash" ]]
[[ ! -e "$snapshot_checkout" && ! -L "$snapshot_checkout" ]]
mkdir -- "$snapshot_checkout"
tar --extract --file "$snapshot_archive" --directory "$snapshot_checkout" --no-same-owner
'@
$importScript = $importScript.Replace("`r`n", "`n")
Invoke-CheckedCommand docker ($dockerArguments + @(
    'exec', $containerId, 'bash', '-c', $importScript, 'yabt-import',
    $containerArchive, $containerCheckout, $archiveHash
))
$runnerExitCode = 1
$copyExitCode = 1
try {
    & docker @dockerArguments exec --env "YABT_SOURCE_REVISION=$commit" $containerId bash `
        "$containerCheckout/tools/linux-acceptance/run-acceptance.sh" $containerResults
    $runnerExitCode = $LASTEXITCODE
}
finally {
    & docker @dockerArguments cp "${containerId}:$containerResults/." $resultDirectory
    $copyExitCode = $LASTEXITCODE
}
$summary = Get-AcceptanceSummary $resultDirectory $commit $runnerExitCode $copyExitCode
Write-JsonFile (Join-Path $resultDirectory 'acceptance-summary.json') $summary
Write-Output "Linux acceptance results: $resultDirectory"
if (-not $summary.accepted) { throw 'Linux acceptance did not pass. Inspect acceptance-summary.json and the retained logs.' }
Write-Output "Accepted: $($summary.counts.passed) passed; $($summary.counts.skipped) individually recorded skips."
