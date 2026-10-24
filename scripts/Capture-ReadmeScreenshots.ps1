#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')
$root = Get-CommunityRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'docs\images' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$fixtureRoot = Join-Path $root ('artifacts\readme-preview\' + [Guid]::NewGuid().ToString('N'))
$executable = Join-Path $fixtureRoot 'ReadmeScreenshots.exe'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$sources = @(Get-CommunitySources -Root $root) + @(Join-Path $PSScriptRoot 'ReadmeScreenshots.cs')
Invoke-CommunityCompile -Compiler (Get-CommunityCompiler) -Sources $sources -Output $executable -EntryPoint 'ReadmeScreenshots' -ConsoleApplication
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $executable
$start.Arguments = '"' + $OutputDirectory + '"'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.EnvironmentVariables['CAMPUSFLOW_COMMUNITY_DATA_DIR'] = Join-Path $fixtureRoot 'data'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Screenshot process did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw 'Screenshot generation timed out.' }
    Write-Host $stdout.GetAwaiter().GetResult()
    $errorOutput = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw ('Screenshot generation failed: ' + $errorOutput) }
}
finally { $process.Dispose() }
