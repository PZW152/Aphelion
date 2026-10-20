<#
.SYNOPSIS
  Builds Aphelion end to end: UI bundle, checks, tests, and a single-file executable.

.PARAMETER SkipTests
  Skip svelte-check and the .NET unit tests (not recommended).

.PARAMETER SelfContained
  Bundle the .NET runtime (larger, but runs without the .NET 10 Desktop Runtime installed).

.EXAMPLE
  pwsh ./build.ps1
  # → artifacts/publish/Aphelion.exe
#>
param(
    [switch]$SkipTests,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$web = Join-Path $root 'src/Aphelion.Web'
$out = Join-Path $root 'artifacts/publish'

function Step($name, [scriptblock]$action) {
    Write-Host "`n== $name" -ForegroundColor DarkYellow
    & $action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
}

Step 'UI dependencies' { npm ci --prefix $web --no-fund --no-audit }
if (-not $SkipTests) { Step 'UI type check' { npm run check --prefix $web } }
Step 'UI bundle' { npm run build --prefix $web }
if (-not $SkipTests) { Step 'Unit tests' { dotnet test (Join-Path $root 'tests/Aphelion.Core.Tests') -c Release --nologo } }

Step 'Publish' {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $sc = if ($SelfContained) { 'true' } else { 'false' }
    dotnet publish (Join-Path $root 'src/Aphelion.App') -c Release -o $out --nologo -p:SelfContained=$sc -p:IncludeNativeLibrariesForSelfExtract=false
}

Get-ChildItem $out | Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } | Format-Table -AutoSize
Write-Host "Done: $out" -ForegroundColor Green
