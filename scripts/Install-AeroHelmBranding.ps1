# AeroHelm GCS product-facing branding overlay.
# Keeps upstream GPL licenses, copyright headers, and source attribution intact.
# Run only after closing Mission Planner; never install a logo from an untrusted path.
param(
    [string]$LogoPath = "",
    [string]$IconPath = "",
    [string]$SplashPath = ""
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtime = Join-Path $root "bin\Release\net461"
$exe = Join-Path $runtime "MissionPlanner.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the AeroHelm Mission Planner fork before applying branding."
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw "Close Mission Planner first to avoid overwriting in-use assets."
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $runtime ("aerohelm-brand-backup-" + $stamp)
New-Item -ItemType Directory -Path $backup -Force | Out-Null

function Set-BrandFile {
    param([string]$Name, [string]$Path)
    $dest = Join-Path $runtime $Name
    if (Test-Path -LiteralPath $dest) {
        Copy-Item -LiteralPath $dest -Destination (Join-Path $backup $Name) -Force
    }
    Copy-Item -LiteralPath $Path -Destination $dest -Force
}

# Existing Mission Planner explicitly supports logo.txt for operator-facing
# app title and its per-product settings XML. This is NOT an attribution rewrite.
$title = Join-Path $runtime 'logo.txt'
if (Test-Path -LiteralPath $title) {
    Copy-Item -LiteralPath $title -Destination (Join-Path $backup 'logo.txt') -Force
}
[IO.File]::WriteAllText($title, "AeroHelm GCS" + [Environment]::NewLine,
    (New-Object System.Text.UTF8Encoding($false)))

# Keep exact user-supplied trademark artwork. We deliberately do not invent it.
foreach ($asset in @(
    @{ name='logo2.png'; path=$LogoPath },
    @{ name='icon.png'; path=$IconPath },
    @{ name='splashbg.png'; path=$SplashPath }
)) {
    if (![string]::IsNullOrWhiteSpace($asset.path)) {
        if (!(Test-Path -LiteralPath $asset.path -PathType Leaf) -or
            [IO.Path]::GetExtension($asset.path).ToLowerInvariant() -ne '.png') {
            throw "$($asset.name) requires an existing PNG file."
        }
        Set-BrandFile -Name $asset.name -Path $asset.path
    }
}
Write-Host "AeroHelm GCS title installed. Original runtime assets backed up under: $backup"
Write-Host "Original Mission Planner configuration stays on disk; the logo.txt product label may select a different settings XML."
Write-Host "Upstream copyright and GPL notices are intentionally retained."
