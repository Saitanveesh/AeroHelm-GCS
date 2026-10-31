# AeroHelm Cloud API smoke test. Windows PowerShell 5.1+; no files or credentials written.
# Usage: .\scripts\Test-AeroHelmCloud.ps1 -CloudUrl https://host.tailnet.ts.net
param(
    [Parameter(Mandatory=$true)]
    [string] $CloudUrl
)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$CloudUrl.TrimEnd('/')
if (!$uri.IsAbsoluteUri -or
    ($uri.Scheme -ne 'https' -and !($uri.Scheme -eq 'http' -and $uri.IsLoopback)) -or
    $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') {
    throw "CloudUrl must be a plain HTTPS origin. HTTP is allowed only on loopback."
}
$base = $uri.GetLeftPart([UriPartial]::Authority)
Write-Host "Testing health endpoint using TLS certificate validation..."
$health = Invoke-RestMethod -Method Get -Uri "$base/health" -TimeoutSec 12 -MaximumRedirection 0
if ($health.status -ne 'ok' -or $health.mode -ne 'hardware-telemetry-only') {
    throw "Cloud must be healthy and running without simulation."
}
Write-Host "PASS: AeroHelm API healthy in hardware-telemetry-only mode."
$cred = Get-Credential -Message 'AeroHelm operator credentials (never pasted into repository)'
if ($null -eq $cred) { throw "No credentials supplied." }
$username = $cred.UserName
$password = $cred.GetNetworkCredential().Password
try {
    $body = @{username=$username;password=$password} | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -TimeoutSec 12 -MaximumRedirection 0 -ContentType 'application/json' -Body $body
} finally {
    $body = $null
    $password = $null
    $cred = $null
}
if (!$response.access_token) { throw "API did not issue an operator token." }
try {
    $headers = @{Authorization="Bearer $($response.access_token)"}
    $vehicles = @(Invoke-RestMethod -Method Get -Uri "$base/api/v1/vehicles" -Headers $headers -TimeoutSec 12 -MaximumRedirection 0)
    Write-Host "PASS: Authenticated aircraft registry request."
    if ($vehicles.Count -eq 0) {
        Write-Host "No aircraft yet: start the authenticated Raspberry Pi MAVLink agent."
    } else {
        # Prints nonsecret high-level status only. No coordinates or tokens in the report.
        $vehicles | Select-Object vehicle_id,source,connection,mode | Format-Table
    }
    Write-Host "This smoke test does NOT connect a MAVLink stream or grant command authority."
} finally {
    $headers = $null
    $response = $null
}
