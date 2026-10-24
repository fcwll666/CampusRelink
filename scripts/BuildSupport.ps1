# Shared, side-effect-free build helpers. Requires Windows PowerShell 5.1 or later.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CommunityRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Get-CommunityCompiler {
    if ($env:OS -ne 'Windows_NT') { throw 'Building CampusRelink requires Windows and .NET Framework 4.8.' }
    $framework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    if ($null -eq $framework -or $framework.Release -lt 528040) { throw '.NET Framework 4.8 or later is required.' }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "C# compiler was not found: $compiler" }
    return $compiler
}

function Get-CommunitySources {
    param([Parameter(Mandatory = $true)][string]$Root)
    $sourceRoot = Join-Path $Root 'src'
    $sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File -Recurse | Sort-Object FullName)
    if ($sources.Count -eq 0) { throw 'No C# source files were found in src.' }
    foreach ($source in $sources) {
        if (($source.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked source files are not allowed: $($source.FullName)" }
    }
    return $sources.FullName
}

function Invoke-CommunityCompile {
    param(
        [Parameter(Mandatory = $true)][string]$Compiler,
        [Parameter(Mandatory = $true)][string[]]$Sources,
        [Parameter(Mandatory = $true)][string]$Output,
        [string]$EntryPoint,
        [string]$IconPath,
        [switch]$ConsoleApplication
    )
    $parent = Split-Path -Parent $Output
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $target = if ($ConsoleApplication) { 'exe' } else { 'winexe' }
    $arguments = @('/nologo', '/langversion:5', '/codepage:65001', '/platform:x64', '/optimize+', '/debug-', ('/target:' + $target), ('/out:' + $Output))
    # Every entry point, including isolated UI tests, reads the same embedded icon.
    # Only the application build supplies IconPath to also set the executable icon.
    $resourceIcon = Join-Path (Get-CommunityRoot) 'branding\CampusRelink\CampusRelink.ico'
    if ($IconPath) { $resourceIcon = [System.IO.Path]::GetFullPath($IconPath) }
    $iconFile = Get-Item -LiteralPath $resourceIcon -ErrorAction Stop
    if ($iconFile.PSIsContainer -or ($iconFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "The application icon must be a regular file: $resourceIcon" }
    $arguments += '/resource:' + $resourceIcon + ',CampusRelink.AppIcon'
    if ($IconPath) { $arguments += '/win32icon:' + $resourceIcon }
    foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Security.dll', 'System.Xml.dll', 'System.Windows.Forms.dll', 'System.Web.dll', 'System.Web.Extensions.dll')) {
        $arguments += '/reference:' + $reference
    }
    if ($EntryPoint) { $arguments += '/main:' + $EntryPoint }
    $compilerOutput = @(& $Compiler @arguments @Sources 2>&1)
    $compilerExit = $LASTEXITCODE
    foreach ($line in $compilerOutput) { Write-Host $line }
    if ($compilerExit -ne 0 -or -not (Test-Path -LiteralPath $Output -PathType Leaf)) { throw "C# compilation failed (exit $compilerExit): $Output" }
}

function Write-CommunityUtf8 {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($true)))
}

function ConvertFrom-CommunityTestOutput {
    param([Parameter(Mandatory = $true)][string]$Suite, [AllowEmptyString()][string]$Output)
    foreach ($line in ($Output -split '\r?\n')) {
        # Plain markers require a separator, so summaries such as "Failures: 0"
        # cannot be mistaken for a failing case.
        if ($line -match '^\s*(?:\[(PASS|FAIL)\]|(PASS|FAIL)(?=\s|:|-))\s*[:\-]?\s*(.+?)\s*$') {
            $status = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
            [pscustomobject]@{ Suite = $Suite; Case = $Matches[3]; Result = $status.ToUpperInvariant() }
        }
    }
}
