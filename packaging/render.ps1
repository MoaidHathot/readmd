<#
.SYNOPSIS
  Renders the Homebrew formula, Scoop manifest and winget manifests for a release from the
  templates in this folder, filling in the version and the SHA-256 of each published archive.

.EXAMPLE
  ./packaging/render.ps1 -Version 0.9.0 -DistDir artifacts/dist -OutDir artifacts/dist/manifests
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $DistDir,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Date = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding $false
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Sha256([string] $rid, [string] $ext) {
    $file = Join-Path $DistDir "readmd-$Version-$rid.$ext"
    if (-not (Test-Path -LiteralPath $file)) { throw "missing archive: $file" }
    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
}

$values = @{
    VERSION          = $Version
    DATE             = $Date
    SHA_WIN_X64      = Sha256 'win-x64' 'zip'
    SHA_WIN_ARM64    = Sha256 'win-arm64' 'zip'
    SHA_LINUX_X64    = Sha256 'linux-x64' 'tar.gz'
    SHA_LINUX_ARM64  = Sha256 'linux-arm64' 'tar.gz'
    SHA_OSX_X64      = Sha256 'osx-x64' 'tar.gz'
    SHA_OSX_ARM64    = Sha256 'osx-arm64' 'tar.gz'
}
$values.SHA_WIN_X64_UPPER = $values.SHA_WIN_X64.ToUpperInvariant()
$values.SHA_WIN_ARM64_UPPER = $values.SHA_WIN_ARM64.ToUpperInvariant()

function Render([string] $templatePath, [string] $outPath) {
    $text = [IO.File]::ReadAllText($templatePath)
    foreach ($kv in $values.GetEnumerator()) { $text = $text.Replace('{{' + $kv.Key + '}}', [string] $kv.Value) }
    $left = [regex]::Matches($text, '\{\{[A-Z_0-9]+\}\}') | ForEach-Object { $_.Value } | Sort-Object -Unique
    if ($left) { throw "unfilled placeholders in $templatePath : $($left -join ', ')" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
    [IO.File]::WriteAllText($outPath, $text.Replace("`r`n", "`n"), $utf8)
    Write-Host "rendered $outPath"
}

Render (Join-Path $here 'homebrew/readmd.rb.template') (Join-Path $OutDir 'readmd.rb')
Render (Join-Path $here 'scoop/readmd.json.template')  (Join-Path $OutDir 'readmd.json')

$wingetDir = Join-Path $OutDir 'winget'
Render (Join-Path $here 'winget/MoaidHathot.readmd.yaml.template')                 (Join-Path $wingetDir 'MoaidHathot.readmd.yaml')
Render (Join-Path $here 'winget/MoaidHathot.readmd.installer.yaml.template')       (Join-Path $wingetDir 'MoaidHathot.readmd.installer.yaml')
Render (Join-Path $here 'winget/MoaidHathot.readmd.locale.en-US.yaml.template')    (Join-Path $wingetDir 'MoaidHathot.readmd.locale.en-US.yaml')

# One zip of the winget manifests for the release page (winget-pkgs PRs take the three files).
$wingetZip = Join-Path $OutDir 'winget.zip'
if (Test-Path -LiteralPath $wingetZip) { Remove-Item -LiteralPath $wingetZip }
Compress-Archive -Path (Join-Path $wingetDir '*') -DestinationPath $wingetZip
Write-Host "rendered $wingetZip"
