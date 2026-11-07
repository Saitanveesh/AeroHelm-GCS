# Vihanga GCS product-facing branding overlay.
# Keeps upstream GPL licenses, copyright headers, and source attribution intact.
# Run only after closing Mission Planner; never install a logo from an untrusted path.
param(
    [string]$LogoPath = "",
    [string]$IconPath = "",
    [string]$SplashPath = "",
    [switch]$SkipLegacySettings
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtime = Join-Path $root "bin\Release\net461"
$exe = Join-Path $runtime "MissionPlanner.exe"
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build the Mission Planner-derived Vihanga GCS before applying branding."
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw "Close Mission Planner first to avoid overwriting in-use assets."
}
# Fail before writing title/settings if explicitly requested artwork is invalid.
foreach($requested in @(
    @{name='logo2.png';path=$LogoPath},
    @{name='icon.png';path=$IconPath},
    @{name='splashbg.png';path=$SplashPath}
)){
    if(-not [string]::IsNullOrWhiteSpace($requested.path) -and
       (-not (Test-Path -LiteralPath $requested.path -PathType Leaf) -or
        [IO.Path]::GetExtension($requested.path).ToLowerInvariant() -ne '.png')){
        throw "$($requested.name) requires an existing PNG file."
    }
}
# Keep branding backups outside the installed runtime to prevent exponential
# growth when a future release snapshots the complete runtime again.
$stamp=(Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0,8)
$backup=Join-Path (Join-Path $root 'build-logs\branding-backups') $stamp
New-Item -ItemType Directory -Path $backup -Force | Out-Null

function Set-BrandFile {
    param([string]$Name, [string]$Path)
    $dest = Join-Path $runtime $Name
    if (Test-Path -LiteralPath $dest) {
        Copy-Item -LiteralPath $dest -Destination (Join-Path $backup $Name) -Force
    }
    Copy-Item -LiteralPath $Path -Destination $dest -Force
}

# The product label selects the native Mission Planner settings XML name.
# Preserve an existing operator configuration before changing that label.
$settingsDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mission Planner'
$newProfile = Join-Path $settingsDir 'Vihanga GCS.xml'
if (-not $SkipLegacySettings -and -not (Test-Path -LiteralPath $newProfile -PathType Leaf)) {
    foreach ($name in @('AeroHelm GCS.xml','config.xml')) {
        foreach ($directory in @($settingsDir,$runtime)) {
            $sourceProfile = Join-Path $directory $name
            if (Test-Path -LiteralPath $sourceProfile -PathType Leaf) {
                New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
                Copy-Item -LiteralPath $sourceProfile -Destination $newProfile -ErrorAction Stop
                Write-Host "Existing settings copied to Vihanga profile: $name (source preserved)."
                break
            }
        }
        if (Test-Path -LiteralPath $newProfile -PathType Leaf) { break }
    }
}
# Existing Mission Planner logo.txt changes the operator-facing title.
$title = Join-Path $runtime 'logo.txt'
if (Test-Path -LiteralPath $title) {
    Copy-Item -LiteralPath $title -Destination (Join-Path $backup 'logo.txt') -Force
}
[IO.File]::WriteAllText($title, "Vihanga GCS" + [Environment]::NewLine,
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
Write-Host "Vihanga GCS title installed. Original runtime assets backed up under: $backup"
if($SkipLegacySettings){
    Write-Host 'Clean profile: skipped importing legacy AeroHelm/Mission Planner settings.'
} else {
    Write-Host 'Existing settings preserved; missing Vihanga profile may be copied from legacy settings.'
}
Write-Host "Upstream copyright and GPL notices are intentionally retained."
