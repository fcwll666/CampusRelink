#requires -Version 5.1
[CmdletBinding()]
param([ValidateRange(5, 300)][int]$TimeoutSeconds = 60)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\BuildSupport.ps1')
& (Join-Path $PSScriptRoot 'scripts\Test-ResultParser.ps1')

$root = Get-CommunityRoot
$compiler = Get-CommunityCompiler
$sources = @(Get-CommunitySources -Root $root)
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $root ('artifacts\tests\' + $runId)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$definitions = @('CommunityCoreTests', 'ConnectionFlowTests', 'StartupProxyTests', 'UiSmokeTests', 'WifiProfilesTests')
$suites = New-Object System.Collections.Generic.List[object]
$caseRows = New-Object System.Collections.Generic.List[object]
$failed = $false

foreach ($entry in $definitions) {
    $suiteRoot = Join-Path $runRoot $entry
    $dataDirectory = Join-Path $suiteRoot 'data'
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $testSource = Join-Path $root ('tests\' + $entry + '.cs')
    $executable = Join-Path $suiteRoot ($entry + '.exe')
    $suite = [ordered]@{ Name = $entry; Passed = $false; ExitCode = $null; TimedOut = $false; DataDirectory = $dataDirectory; Stdout = ''; Stderr = ''; Error = ''; Cases = @() }
    try {
        if (-not (Test-Path -LiteralPath $testSource -PathType Leaf)) { throw "Missing required test source: tests\$entry.cs" }
        Invoke-CommunityCompile -Compiler $compiler -Sources ($sources + @($testSource)) -Output $executable -EntryPoint $entry -ConsoleApplication
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = $executable
        $start.WorkingDirectory = $suiteRoot
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $start.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $start.EnvironmentVariables['CAMPUSFLOW_COMMUNITY_DATA_DIR'] = $dataDirectory
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $start
        try {
            if (-not $process.Start()) { throw "Could not start isolated test suite $entry." }
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $suite.TimedOut = $true
                $process.Kill()
                $process.WaitForExit()
            }
            $suite.Stdout = $stdoutTask.GetAwaiter().GetResult()
            $suite.Stderr = $stderrTask.GetAwaiter().GetResult()
            $suite.ExitCode = $process.ExitCode
        }
        finally { $process.Dispose() }
        $cases = New-Object System.Collections.Generic.List[object]
        foreach ($case in @(ConvertFrom-CommunityTestOutput -Suite $entry -Output $suite.Stdout)) {
            $cases.Add($case)
            $caseRows.Add($case)
        }
        $suite.Cases = @($cases.ToArray())
        $badCases = @($cases | Where-Object { $_.Result -eq 'FAIL' })
        $suite.Passed = ($suite.ExitCode -eq 0 -and -not $suite.TimedOut -and $cases.Count -gt 0 -and $badCases.Count -eq 0)
        if ($cases.Count -eq 0) { $suite.Error = 'The suite did not emit any PASS/FAIL case results.' }
        if ($suite.TimedOut) { $suite.Error = 'The isolated test process exceeded its timeout.' }
    }
    catch { $suite.Error = $_.Exception.Message }
    if (-not $suite.Passed) {
        $failed = $true
        $caseRows.Add([pscustomobject]@{ Suite = $entry; Case = 'Suite completion: ' + $suite.Error; Result = 'FAIL' })
    }
    Write-CommunityUtf8 -Path (Join-Path $suiteRoot 'stdout.txt') -Content $suite.Stdout
    Write-CommunityUtf8 -Path (Join-Path $suiteRoot 'stderr.txt') -Content $suite.Stderr
    $suites.Add([pscustomobject]$suite)
    $label = if ($suite.Passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0} {1} ({2} cases)" -f $label, $entry, $suite.Cases.Count)
    if ($suite.Stdout) { Write-Host $suite.Stdout.TrimEnd() }
    if ($suite.Stderr) { Write-Host $suite.Stderr.TrimEnd() }
    if ($suite.Error) { Write-Host $suite.Error }
}

$report = [ordered]@{ RunId = $runId; CompletedUtc = [DateTime]::UtcNow.ToString('o'); Passed = (-not $failed); Suites = @($suites.ToArray()) }
Write-CommunityUtf8 -Path (Join-Path $runRoot 'results.json') -Content ($report | ConvertTo-Json -Depth 8)
$caseRows | Export-Csv -LiteralPath (Join-Path $runRoot 'results.csv') -NoTypeInformation -Encoding UTF8
Write-Host "Test reports: $runRoot"
if ($failed) { throw 'One or more isolated test suites failed. See results.json and results.csv.' }
