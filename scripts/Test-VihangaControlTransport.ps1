# Non-actuating Windows -> Oracle -> Pi -> Oracle control-plane test.
# No MAVLink commands, UART writes or motor actions are possible from this script.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Origin,
    [string]$VehicleId = "AH-HW-001",
    [string]$PilotUsername = "pilot1",
    [ValidateRange(1, 20)]
    [int]$MaxPolls = 12
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = $null
if (-not [Uri]::TryCreate($Origin, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne "https" -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
    $uri.AbsolutePath -ne "/" -or
    $VehicleId -notmatch "^[A-Za-z0-9_-]{3,64}$" -or
    $PilotUsername -notmatch "^[A-Za-z0-9_-]{3,64}$") {
    throw "Use a private HTTPS relay origin (no path/query/credentials) and valid identifiers."
}
$base = $Origin.TrimEnd("/")
$status = Invoke-RestMethod -Uri "$base/api/v1/flight-control/status" -Method Get -TimeoutSec 10
if ($status.mode -ne "SIMULATOR_AUTHORITY_ONLY" -or $status.pilot_to_pi_shadow_probe -ne $true) {
    throw "Shadow probe unavailable. Live telemetry does not require control simulator mode."
}
if ($status.physical_command_dispatch -ne $false -or $status.native_mavlink_command_tunnel -ne $false) {
    throw "Unsafe server capability reported; refusing diagnostic."
}

$secure = Read-Host "Simulator pilot password (not telemetry password)" -AsSecureString
try {
    $credential = New-Object System.Management.Automation.PSCredential($PilotUsername, $secure)
    $password = $credential.GetNetworkCredential().Password
    $body = @{ username = $PilotUsername; password = $password; role = "pilot" } | ConvertTo-Json -Compress
    $password = $null
    $login = Invoke-RestMethod -Uri "$base/api/v1/flight-control/auth/login" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 10
} finally {
    $password = $null
    $body = $null
    $credential = $null
    $secure = $null
}
if (-not $login.access_token) {
    throw "Pilot simulator login failed."
}
$headers = @{ Authorization = "Bearer $($login.access_token)" }
$route = "$base/api/v1/vehicles/$VehicleId/control/transport/probe"
$started = Invoke-RestMethod -Uri $route -Method Post -Headers $headers -TimeoutSec 10
if (-not $started.request_id -or $started.physical_command_dispatch -ne $false) {
    throw "Unexpected diagnostic response: no command may be dispatched."
}
$receipt = $null
for ($i = 0; $i -lt $MaxPolls; $i++) {
    Start-Sleep -Milliseconds 300
    $receipt = Invoke-RestMethod -Uri "$route/$($started.request_id)" -Method Get -Headers $headers -TimeoutSec 10
    if ($receipt.status -ne "WAITING_FOR_PI") { break }
}
if ($receipt.status -ne "PI_ECHO_NO_ACTUATION" -or
    $receipt.pi_transport_confirmed -ne $true -or
    $receipt.uart_touched -ne $false -or
    $receipt.fc_ack_received -ne $false -or
    $receipt.flight_control_enabled -ne $false) {
    throw "Shadow round trip failed or was inconclusive: $($receipt.status)"
}
$receipt | Select-Object request_id, vehicle_id, control_epoch, status, pi_transport_confirmed, uart_touched, fc_ack_received, physical_command_dispatch, flight_control_enabled | Format-List
Write-Output "PASS: Pilot-to-Pi private transport round-trip only. NO FLIGHT CONTROL VERIFIED."
