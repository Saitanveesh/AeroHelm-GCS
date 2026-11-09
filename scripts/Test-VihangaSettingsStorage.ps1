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
    if (-not ([Convert]::ToBase64String($payload) -eq [Convert]::ToBase64String($actual))) {
        throw 'Local profile write/read integrity check failed.'
    }
    Write-Host "PROFILE STORAGE READY: $directory" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $probe -PathType Leaf) {
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
    }
}

# Exercise the precise .tlog/.rlog CreateNew / exclusive-share semantics
# used by Mission Planner, not just a generic profile .tmp write.
$logs = Join-Path $directory 'logs'
New-Item -ItemType Directory -Path $logs -Force -ErrorAction Stop | Out-Null
$nonce = [Guid]::NewGuid().ToString('N')
foreach ($ext in @('tlog','rlog')) {
    $target = Join-Path $logs ('.exclusive-probe-' + $nonce + '.' + $ext)
    try {
        $handle = [IO.File]::Open($target,
            [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes('Vihanga flight-data log preflight')
            $handle.Write($bytes, 0, $bytes.Length)
            $handle.Flush()
        }
        finally {
            $handle.Dispose()
        }
    }
    finally {
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force -ErrorAction Stop
        }
    }
}
Write-Host "TELEMETRY LOG STORAGE READY: $logs" -ForegroundColor Green
