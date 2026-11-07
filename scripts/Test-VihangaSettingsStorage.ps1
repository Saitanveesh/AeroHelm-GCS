# Validate that the current Windows user can persist Vihanga preferences.
# This never reads, replaces or deletes an existing operator profile.
$ErrorActionPreference = 'Stop'
$local = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($local)) {
    throw 'LocalApplicationData is not available for the current Windows account.'
}
$directory = Join-Path $local 'Vihanga GCS'
New-Item -Path $directory -ItemType Directory -Force -ErrorAction Stop | Out-Null
$probe = Join-Path $directory ('.storage-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $payload = [Text.Encoding]::UTF8.GetBytes('Vihanga GCS local-profile write preflight')
    [IO.File]::WriteAllBytes($probe, $payload)
    $actual = [IO.File]::ReadAllBytes($probe)
    if (-not [Linq.Enumerable]::SequenceEqual[byte]($payload, $actual)) {
        throw 'Local profile write/read integrity check failed.'
    }
    Write-Host "PROFILE STORAGE READY: $directory" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $probe -PathType Leaf) {
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
    }
}
