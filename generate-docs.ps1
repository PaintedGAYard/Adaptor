<#
.SYNOPSIS
    Generates the Adaptor API documentation using DocFX.
.DESCRIPTION
    This script installs DocFX (if not already installed) and builds
    the documentation site from XML doc comments in the source projects.
    Config and content live under src/doc-gen/; output goes to doc/.
.PARAMETER Serve
    If specified, starts a local HTTP server after building to preview the docs.
.PARAMETER Clean
    If specified, removes the doc/ directory before building.
.EXAMPLE
    .\generate-docs.ps1
    .\generate-docs.ps1 -Serve
    .\generate-docs.ps1 -Clean -Serve
#>

param(
    [switch]$Serve,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$RepoRoot   = Resolve-Path "$PSScriptRoot"
$DocGenDir  = Join-Path $RepoRoot "src\doc-gen"
$OutputDir  = Join-Path $RepoRoot "Doc"

# ---- Clean (optional) ----
if ($Clean) {
    Write-Host "Cleaning previous output..." -ForegroundColor Yellow
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
    }
}

# ---- Ensure DocFX is available ----
$docfx = $null

# Check for dotnet tool (preferred)
$dotnetDocfx = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($dotnetDocfx) {
    $toolList = dotnet tool list --global 2>$null
    if ($toolList -match "docfx") {
        $docfx = "docfx"
    } else {
        Write-Host "Installing DocFX as a global dotnet tool..." -ForegroundColor Green
        dotnet tool install --global docfx
        if ($LASTEXITCODE -eq 0) {
            $docfx = "docfx"
        }
    }
}

# Fallback: check for standalone docfx.exe on PATH
if (-not $docfx) {
    $standalone = Get-Command "docfx" -ErrorAction SilentlyContinue
    if ($standalone) {
        $docfx = "docfx"
    }
}

if (-not $docfx) {
    Write-Host @"

ERROR: DocFX is not available.
Please install it manually:
  1. Via .NET tool (recommended):
     dotnet tool install --global docfx
  2. Or download from: https://github.com/dotnet/docfx/releases
"@ -ForegroundColor Red
    exit 1
}

Write-Host "Using DocFX: $docfx" -ForegroundColor Cyan

# ---- Build documentation ----
Push-Location $RepoRoot
try {
    Write-Host "`nGenerating API metadata..." -ForegroundColor Green
    & $docfx metadata docfx.json
    if ($LASTEXITCODE -ne 0) {
        throw "docfx metadata failed with exit code $LASTEXITCODE"
    }

    Write-Host "`nBuilding documentation site..." -ForegroundColor Green
    & $docfx build docfx.json --output "$OutputDir"
    if ($LASTEXITCODE -ne 0) {
        throw "docfx build failed with exit code $LASTEXITCODE"
    }

    Write-Host "`nDocumentation generated successfully!" -ForegroundColor Green
    Write-Host "Output: $OutputDir" -ForegroundColor Cyan

    # ---- Serve (optional) ----
    if ($Serve) {
        Write-Host "`nStarting preview server at http://localhost:8080 ..." -ForegroundColor Yellow
        & $docfx serve "$OutputDir"
    }
}
finally {
    Pop-Location
}
