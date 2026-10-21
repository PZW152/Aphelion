<#
.SYNOPSIS
  Builds a release and the signed update manifest; with -Publish, creates the GitHub release.

.DESCRIPTION
  1. Checks the working tree is clean and the version matches Directory.Build.props.
  2. Runs build.ps1 (UI checks, unit tests, single-file publish).
  3. Writes artifacts/release/<version>/: Aphelion.exe, update.json, update.json.sig, SHA256SUMS.txt.
     update.json pins the executable's size and SHA-256 and lists where it can be downloaded;
     update.json.sig is an ECDSA P-256 signature (IEEE P1363, base64) over update.json's exact bytes.
  4. -Publish: tags v<version> and uploads the files with `gh release create`.

  The private signing key never enters the repository. Default location: %USERPROFILE%\.aphelion\update-signing-key.pem,
  or set APHELION_SIGNING_KEY. Back it up: without it, later releases cannot be offered as updates to existing copies.

.EXAMPLE
  pwsh tools/release.ps1 -Version 0.2.0
  pwsh tools/release.ps1 -Version 0.2.0 -Publish
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Repository = 'PZW152/Aphelion',
    [string] $KeyPath = ($env:APHELION_SIGNING_KEY ?? (Join-Path $env:USERPROFILE '.aphelion\update-signing-key.pem')),
    # Base URL the manifest names for the executable; defaults to the GitHub release asset.
    [string] $DownloadBase,
    # A prebuilt executable (test releases); skips the build and the Directory.Build.props check.
    [string] $Executable,
    [string] $NotesDir,
    [string] $OutDir,
    [switch] $SkipBuild,
    [switch] $Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Executable) {
    $declared = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($declared -ne $Version) { throw "Directory.Build.props says $declared, not $Version" }
}
if ($Publish -and (git status --porcelain)) { throw 'The working tree has uncommitted changes.' }
if (-not (Test-Path $KeyPath)) { throw "Signing key not found: $KeyPath" }

$notesDir = $NotesDir ? $NotesDir : (Join-Path $root "docs/releases/$Version")
$notes = [ordered]@{}
foreach ($lang in 'zh-Hant', 'zh-Hans', 'en') {
    $file = Join-Path $notesDir "$lang.md"
    if (-not (Test-Path $file)) { throw "Release notes missing: $file" }
    $notes[$lang] = (Get-Content $file -Raw -Encoding utf8).Trim()
}

if (-not $SkipBuild -and -not $Executable) {
    pwsh -NoProfile -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build.ps1 failed' }
}

$exe = $Executable ? $Executable : (Join-Path $root 'artifacts/publish/Aphelion.exe')
$built = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
if ($built -ne $Version) { throw "The built executable is $built, not $Version" }

$out = $OutDir ? $OutDir : (Join-Path $root "artifacts/release/$Version")
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null
Copy-Item $exe $out
$exeOut = Join-Path $out 'Aphelion.exe'
$sha256 = (Get-FileHash $exeOut -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $exeOut).Length
$base = $DownloadBase ? $DownloadBase.TrimEnd('/') : "https://github.com/$Repository/releases/download/v$Version"

$manifest = [ordered]@{
    schema    = 1
    product   = '6E1C2F0B-4A7D-4C55-9C6B-AF3E1D0A9F21'   # AppInfo.AppId
    version   = $Version
    published = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)   # never the ROC calendar of zh-TW
    file      = [ordered]@{ name = 'Aphelion.exe'; size = $size; sha256 = $sha256; urls = @("$base/Aphelion.exe") }
    notes     = $notes
    page      = "https://github.com/$Repository/releases/tag/v$Version"
}
$json = $manifest | ConvertTo-Json -Depth 6
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
[System.IO.File]::WriteAllBytes((Join-Path $out 'update.json'), $bytes)

$key = [System.Security.Cryptography.ECDsa]::Create()
$key.ImportFromPem((Get-Content $KeyPath -Raw))
$signature = $key.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
Set-Content (Join-Path $out 'update.json.sig') ([Convert]::ToBase64String($signature)) -NoNewline -Encoding ascii

# The signature must verify with the public key compiled into the application.
$pub = [regex]::Match((Get-Content (Join-Path $root 'src/Aphelion.Core/Update/Updates.cs') -Raw), '-----BEGIN PUBLIC KEY-----[\s\S]+?-----END PUBLIC KEY-----').Value -replace '(?m)^\s+', ''
$verifier = [System.Security.Cryptography.ECDsa]::Create()
$verifier.ImportFromPem($pub)
if (-not $verifier.VerifyData($bytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
    throw 'The signing key does not match the public key compiled into Aphelion.'
}

Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Release $Version ready in $out"
Get-ChildItem $out | Format-Table Name, Length -AutoSize

if ($Publish) {
    $notesFile = Join-Path $out 'RELEASE_NOTES.md'
    @($notes['zh-Hant'], '', '---', '', $notes['en']) | Set-Content $notesFile -Encoding utf8
    gh release create "v$Version" --repo $Repository --title "Aphelion $Version" --notes-file $notesFile `
        $exeOut (Join-Path $out 'update.json') (Join-Path $out 'update.json.sig') (Join-Path $out 'SHA256SUMS.txt')
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
