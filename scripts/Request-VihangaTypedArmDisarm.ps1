# Pilot+independent approver ARM/DISARM request through Vihanga's typed API.
# This is NOT raw MAVLink injection. It does not bypass flight-controller
# arming checks and it will refuse to actuate until real Pi physical evidence
# and an independent local safety interlock are implemented and certified.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("ARM", "DISARM")]
    [string]$Action,
    [Parameter(Mandatory=$true)]
    [string]$PlanFile,
    [string]$Origin = "https://aerohelm-vcn.tailf3ab6a.ts.net:8443",
    [string]$VehicleId = "AH-HW-001",
    [string]$PilotUsername = "pilot1",
    [string]$ApproverUsername = "safety1"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$parsed = $null
if (-not [Uri]::TryCreate($Origin, [UriKind]::Absolute, [ref]$parsed) -or
    $parsed.Scheme -ne "https" -or
    -not $parsed.Host.EndsWith(".ts.net", [StringComparison]::OrdinalIgnoreCase) -or
    $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment -or
    $parsed.AbsolutePath -ne "/" -or $VehicleId -notmatch "^[A-Za-z0-9_-]{3,64}$") {
    throw "Refusing untrusted control origin or vehicle ID."
}
if (-not (Test-Path -LiteralPath $PlanFile -PathType Leaf)) {
    throw "A real written bench safety plan file is required."
}
$planDigest = (Get-FileHash -LiteralPath $PlanFile -Algorithm SHA256).Hash.ToLowerInvariant()
$base = $parsed.GetLeftPart([UriPartial]::Authority)
$state = Invoke-RestMethod -Uri "$base/api/v1/flight-control/status" -TimeoutSec 10
if ($state.mode -ne "SIMULATOR_AUTHORITY_ONLY" -or
    $state.typed_arm_disarm_downlink_configured -ne $true -or
    $state.native_mavlink_command_tunnel -ne $false) {
    throw "Typed downlink/role authorization is unavailable; refusing all actions."
}
Write-Host "ACTION: $Action | AIRCRAFT: $VehicleId"
Write-Host "PLAN SHA256: $planDigest"
Write-Host "Hardware readiness must be established by the Pi and FC independently."
$confirmation = Read-Host "Type EXACTLY: REQUEST $Action $VehicleId"
if ($confirmation -cne "REQUEST $Action $VehicleId") {
    throw "Operator confirmation mismatch: no command submitted."
}
function Get-RoleHeader([string]$Role, [string]$Username) {
    $secure = Read-Host "Enter independent $Role password" -AsSecureString
    $credential = New-Object System.Management.Automation.PSCredential($Username, $secure)
    $password = $credential.GetNetworkCredential().Password
    try {
        $body = @{ username=$Username; password=$password; role=$Role } |
            ConvertTo-Json -Compress
        $reply = Invoke-RestMethod -Uri "$base/api/v1/flight-control/auth/login" `
            -Method Post -ContentType "application/json" -Body $body -TimeoutSec 10
        if (-not $reply.access_token) { throw "Missing $Role token" }
        return @{ Authorization="Bearer $($reply.access_token)" }
    } finally {
        $password = $null
        $body = $null
        $credential = $null
        $secure = $null
    }
}
$approvalHeaders = Get-RoleHeader -Role "approver" -Username $ApproverUsername
$controlPath = "$base/api/v1/vehicles/$VehicleId/control"
$approval = Invoke-RestMethod -Uri "$controlPath/approvals" -Method Post `
    -Headers $approvalHeaders -ContentType "application/json" `
    -Body (@{ plan_sha256=$planDigest } | ConvertTo-Json -Compress) -TimeoutSec 10
$approvalHeaders = $null
$pilotHeaders = Get-RoleHeader -Role "pilot" -Username $PilotUsername
$lease = Invoke-RestMethod -Uri "$controlPath/lease" -Method Post `
    -Headers $pilotHeaders -ContentType "application/json" `
    -Body (@{plan_sha256=$planDigest; approval_id=$approval.approval_id} | ConvertTo-Json -Compress) -TimeoutSec 10
$id = ([guid]::NewGuid()).ToString("N")
$body = @{
    request_id=$id; sequence=1; action=$Action; params=@{};
    plan_sha256=$planDigest; ttl_ms=2000
} | ConvertTo-Json -Compress
$pilotHeaders["X-Control-Lease"] = $lease.lease_token
try {
    $initial = Invoke-RestMethod -Uri "$controlPath/commands" -Method Post `
        -Headers $pilotHeaders -ContentType "application/json" `
        -Body $body -TimeoutSec 10
    Write-Host "REQUEST RECEIPT: $($initial.status)"
    $pilotHeaders.Remove("X-Control-Lease")
    $final = $initial
    for ($i=0; $i -lt 12; $i++) {
        if ($final.status -notin @("SENT_TO_PI","PI_RECEIVED","FC_IN_PROGRESS")) { break }
        Start-Sleep -Milliseconds 250
        $final = Invoke-RestMethod -Uri "$controlPath/commands/$id" `
            -Headers $pilotHeaders -TimeoutSec 10
    }
    $final | Select-Object request_id,action,status,pi_received,pi_interlock_verified,fc_ack_result,flight_executed | Format-List
    if ($final.status -eq "FC_ACCEPTED_NOT_EXECUTION_VERIFIED") {
        Write-Warning "FC accepted the MAVLink command. Actual armed state remains UNVERIFIED until a new real FC HEARTBEAT confirms it."
    } elseif ($final.status -eq "AUTHORIZED_NOT_DISPATCHED" -or $final.status -eq "PI_REFUSED") {
        throw "Control request did not actuate: $($final.status)"
    } elseif ($final.status -ne "FC_ACCEPTED_NOT_EXECUTION_VERIFIED") {
        throw "Control request not accepted/confirmed: $($final.status)"
    }
} finally {
    try {
        $pilotHeaders.Remove("X-Control-Lease")
        Invoke-RestMethod -Uri "$controlPath/revoke" -Method Post -Headers $pilotHeaders -TimeoutSec 10 | Out-Null
    } catch {
        Write-Warning "Remote lease revocation failed. Lease expires automatically; do not retry the command."
    }
    $body = $null
    $pilotHeaders = $null
}
