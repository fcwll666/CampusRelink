#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Release')][string]$Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\BuildSupport.ps1')

$root = Get-CommunityRoot
$compiler = Get-CommunityCompiler
$sources = @(Get-CommunitySources -Root $root)
$output = Join-Path $root 'artifacts\build\CampusRelink.exe'
$icon = Join-Path $root 'branding\CampusRelink\CampusRelink.ico'
Invoke-CommunityCompile -Compiler $compiler -Sources $sources -Output $output -IconPath $icon
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($output).FileVersion
if ($version -ne '1.0.3.0') { throw "Unexpected executable version '$version'; expected 1.0.3.0." }
Write-Host "Built $output ($version, x64, $Configuration)."
