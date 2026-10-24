#requires -Version 5.1
[CmdletBinding()]
param([switch]$SkipValidation)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\BuildSupport.ps1')

$root = Get-CommunityRoot
if (-not $SkipValidation) {
    & (Join-Path $root 'Test.ps1')
    & (Join-Path $root 'build.ps1') -Configuration Release
}
else { Write-Warning 'SkipValidation was requested. Existing build output will be packaged without rerunning tests or compilation.' }

$executable = Join-Path $root 'artifacts\build\CampusRelink.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'No build output was found. Run build.ps1 first.' }
if ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion -ne '1.0.3.0') { throw 'Only CampusRelink.exe version 1.0.3.0 may be packaged by this release script.' }
$stageRoot = Join-Path $root ('artifacts\package\' + [Guid]::NewGuid().ToString('N'))
$sourceStage = Join-Path $stageRoot 'source'
$binaryStage = Join-Path $stageRoot 'binary'
$dist = Join-Path $root 'dist'
foreach ($directory in @($sourceStage, $binaryStage, $dist)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

# Every published file is named here or explicitly selected as C# source below.
# Never replace this with Copy-Item -Recurse on the project directory.
$sourceManifest = @(
    '.gitignore', '.gitattributes', '.editorconfig', '.github/workflows/build.yml', '.github/dependabot.yml',
    '.github/ISSUE_TEMPLATE/bug_report.yml', '.github/ISSUE_TEMPLATE/feature_request.yml', '.github/ISSUE_TEMPLATE/config.yml',
    '.github/PULL_REQUEST_TEMPLATE.md', 'build.ps1', 'Test.ps1', 'Package.ps1',
    'README.md', 'README.en.md', 'CHANGELOG.md', 'SECURITY.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'NOTICE.md', 'LICENSE',
    'docs/PORTAL-CONFIGURATION.md', 'docs/INSTALLATION.md', 'docs/FAQ.md', 'docs/QUICKSTART.md',
    'docs/DEVELOPMENT.md', 'docs/RELEASING.md', 'docs/PUBLISHING.md', 'docs/README-DESIGN.md',
    'docs/images/hero.png', 'docs/images/hero.svg', 'docs/images/main-light.png', 'docs/images/main-dark.png', 'docs/images/about.png',
    'scripts/BuildSupport.ps1', 'scripts/Test-ResultParser.ps1', 'scripts/Capture-ReadmeScreenshots.ps1',
    'scripts/ReadmeScreenshots.cs', 'scripts/Build-ReadmeHero.py',
    'branding/CampusRelink/README.md', 'branding/CampusRelink/build_brand_assets.py', 'branding/CampusRelink/asset-check.json',
    'branding/CampusRelink/CampusRelink.ico', 'branding/CampusRelink/CampusRelink-icon.svg', 'branding/CampusRelink/CampusRelink-icon.png',
    'branding/CampusRelink/CampusRelink-symbol.svg', 'branding/CampusRelink/CampusRelink-tray-white.png', 'branding/CampusRelink/CampusRelink-tray-dark.png',
    'branding/CampusRelink/CampusRelink-wordmark.svg', 'branding/CampusRelink/CampusRelink-wordmark.png', 'branding/CampusRelink/CampusRelink-preview.png',
    'tests/CommunityCoreTests.cs', 'tests/ConnectionFlowTests.cs', 'tests/StartupProxyTests.cs', 'tests/UiSmokeTests.cs', 'tests/WifiProfilesTests.cs'
)
foreach ($source in @(Get-CommunitySources -Root $root)) {
    $sourceManifest += $source.Substring($root.Length + 1).Replace('\', '/')
}
$sourceManifest = @($sourceManifest | Sort-Object -Unique)

function Copy-ManifestFile {
    param([string]$RelativePath, [string]$SourceRoot, [string]$DestinationRoot)
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[/\\])\.\.([/\\]|$)') { throw "Unsafe manifest path: $RelativePath" }
    $sourcePath = Join-Path $SourceRoot $RelativePath
    $item = Get-Item -LiteralPath $sourcePath -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Only regular files may be packaged: $RelativePath" }
    $targetPath = Join-Path $DestinationRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $targetPath
}

function Assert-CleanStage {
    param([string]$Directory, [string[]]$Manifest)
    $actual = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | ForEach-Object { $_.FullName.Substring($Directory.Length + 1).Replace('\', '/') } | Sort-Object)
    $difference = @(Compare-Object -ReferenceObject @($Manifest | Sort-Object) -DifferenceObject $actual)
    if ($difference.Count -ne 0) { throw "Staging content differs from the approved file manifest: $Directory" }
    foreach ($relative in $actual) {
        if ($relative -match '(^|/)(artifacts|dist|diag|data|\.git|\.codex)(/|$)' -or $relative -match '(?i)(credentials|settings\.xml|selftest\.out|startup-proxy-(backup|check)|\.log$|\.pfx$|\.pem$|\.key$|\.env$|baseline.*\.json$)') {
            throw "Private or generated data is forbidden in a release: $relative"
        }
    }
}

foreach ($relative in $sourceManifest) { Copy-ManifestFile -RelativePath $relative -SourceRoot $root -DestinationRoot $sourceStage }
Copy-Item -LiteralPath $executable -Destination (Join-Path $binaryStage 'CampusRelink.exe')
# The downloadable app gets a concise guide without source-only image/doc links.
$quickReadme = [System.IO.File]::ReadAllText((Join-Path $sourceStage 'docs/QUICKSTART.md'))
$quickReadme = $quickReadme.Replace('(PORTAL-CONFIGURATION.md)', '(docs/PORTAL-CONFIGURATION.md)').Replace('(FAQ.md)', '(docs/FAQ.md)').Replace('(../LICENSE)', '(LICENSE)').Replace('(../NOTICE.md)', '(NOTICE.md)')
Write-CommunityUtf8 -Path (Join-Path $binaryStage 'README.md') -Content $quickReadme
$binaryDocs = @('docs/PORTAL-CONFIGURATION.md', 'docs/FAQ.md', 'docs/INSTALLATION.md', 'docs/DEVELOPMENT.md',
    'docs/RELEASING.md', 'docs/PUBLISHING.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'SECURITY.md', 'LICENSE', 'NOTICE.md')
$binaryManifest = @('CampusRelink.exe', 'README.md') + $binaryDocs
foreach ($relative in $binaryDocs) { Copy-ManifestFile -RelativePath $relative -SourceRoot $root -DestinationRoot $binaryStage }
Assert-CleanStage -Directory $sourceStage -Manifest $sourceManifest
Assert-CleanStage -Directory $binaryStage -Manifest $binaryManifest

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archives = @(
    @{ Directory = $sourceStage; Name = 'CampusRelink-1.0.3-source.zip' },
    @{ Directory = $binaryStage; Name = 'CampusRelink-1.0.3-win-x64.zip' }
)
$checksums = New-Object System.Collections.Generic.List[string]
foreach ($archive in $archives) {
    $target = Join-Path $dist $archive.Name
    $temporary = Join-Path $dist ($archive.Name + '.' + [Guid]::NewGuid().ToString('N') + '.partial')
    [System.IO.Compression.ZipFile]::CreateFromDirectory($archive.Directory, $temporary, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporary -Destination $target -Force
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums.Add($hash + '  ' + $archive.Name)
    Write-Host "Packaged $target"
}
[System.IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS'), (($checksums.ToArray() -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
Write-Host "Release packages and SHA256SUMS are ready in $dist. No application entry point was launched."
