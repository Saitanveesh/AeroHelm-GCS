# AeroHelm Windows private-cloud + Pi read-only preflight. No flight commands.
# Only POST is for operator login; all other requests use GET.
param(
    [Parameter(Mandatory=$true)][string]$CloudUrl,
    [string]$VehicleId = '',
    [switch]$RequireConnectedPi,
    [switch]$RequireCompleteParameters
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$branch=(& git -C $root branch --show-current).Trim()
$commit=(& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') {
    throw 'Unknown Git source commit; cannot document this preflight.'
}
if ($branch -notin @('master','feat/vihanga-phase3-native-toolbar')) {
    throw 'Use the reviewed Vihanga master or development branch.'
}
$uri=$null
if (-not [Uri]::TryCreate($CloudUrl,[UriKind]::Absolute,[ref]$uri) -or
    $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or
    -not (($uri.Scheme -eq 'https' -and
           ($uri.Host.EndsWith('.ts.net',[StringComparison]::OrdinalIgnoreCase) -or
            $uri.IsLoopback)) -or
          ($uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
    throw 'CloudUrl must be a private Tailscale HTTPS or loopback origin without credentials or path.'
}
if ($VehicleId -and $VehicleId -notmatch '^[A-Za-z0-9_-]{3,64}$') {
    throw 'Invalid aircraft identifier.'
}
$origin=$uri.GetLeftPart([UriPartial]::Authority)
$report=[ordered]@{
    schema='aerohelm.private-preflight.v1'
    time_utc=[DateTime]::UtcNow.ToString('o')
    gcs_branch=$branch
    gcs_commit=$commit
    requires_connected_pi=[bool]$RequireConnectedPi
    requires_complete_parameters=[bool]$RequireCompleteParameters
    cloud_healthy=$false
    authenticated=$false
    non_actuating_policy=$false
    audit_chain_verified_now=$false
    pi_fresh_link_reported=$false
    registered_aircraft_count=0
    selected_aircraft_found=$false
    selected_aircraft_fresh=$false
    parameter_cache_verified_complete=$false
    parameter_capture_recent=$false
    errors=@()
    release_authorized=$false
    physical_flight_authorized=$false
}
function Get-Private([string]$relative,[hashtable]$auth=$null) {
    $args=@{Method='Get';Uri=("$origin/"+$relative.TrimStart('/'));
        TimeoutSec=12;MaximumRedirection=0;ErrorAction='Stop'}
    if ($auth) { $args['Headers']=$auth }
    return Invoke-RestMethod @args
}
$credential=$null; $password=$null; $body=$null; $token=$null; $headers=$null
$failed=$false
try {
    Write-Host '[1/6] Verify private hardware-only API health'
    $health=Get-Private 'health'
    if ($health.status -ne 'ok' -or $health.mode -ne 'hardware-telemetry-only') {
        throw 'Unexpected service mode: stop before Pi testing.'
    }
    $report.cloud_healthy=$true

    Write-Host '[2/6] Interactive operator authentication'
    $credential=Get-Credential -Message 'AeroHelm private read-only preflight'
    if ($null -eq $credential) { throw 'Authentication cancelled.' }
    $password=$credential.GetNetworkCredential().Password
    $body=@{username=$credential.UserName;password=$password} | ConvertTo-Json -Compress
    $loginArgs=@{Method='Post';Uri="$origin/api/v1/auth/login";
        ContentType='application/json';Body=$body;TimeoutSec=12;
        MaximumRedirection=0;ErrorAction='Stop'}
    $login=Invoke-RestMethod @loginArgs
    $password=$null;$body=$null;$credential=$null
    if (-not $login.access_token) { throw 'No signed login token.' }
    $token=$login.access_token
    $headers=@{Authorization="Bearer $token"}
    $report.authenticated=$true

    Write-Host '[3/6] Verify hardware command prohibition and current audit chain'
    $policy=Get-Private 'api/v2/security/posture' $headers
    if ($policy.mission_actuation_enabled -ne $false -or
        $policy.arbitrary_mavlink_forwarding -ne $false) {
        throw 'Hardware command policy unexpectedly enabled.'
    }
    $report.non_actuating_policy=$true
    $audit=Get-Private 'api/v2/security/audit?limit=75' $headers
    $report.audit_chain_verified_now=($audit.current_chain_verified -eq $true)
    if (-not $report.audit_chain_verified_now) { throw 'Audit chain integrity check failed.' }

    Write-Host '[4/6] Observe fleet and fresh MAVLink without issuing a command'
    $fleet=Get-Private 'api/v2/fleet' $headers
    if ($fleet.fleet_control_enabled -ne $false) {
        throw 'Unexpected fleet-control permission.'
    }
    $vehicles=@($fleet.vehicles)
    $report.registered_aircraft_count=$vehicles.Count
    if (-not $VehicleId -and $vehicles.Count -eq 1) {
        $VehicleId=[string]$vehicles[0].vehicle_id
    }
    if ($VehicleId) {
        $matching=@($vehicles | Where-Object { $_.vehicle_id -eq $VehicleId })
        $report.selected_aircraft_found=($matching.Count -eq 1)
        if ($matching.Count -eq 1) {
            $report.selected_aircraft_fresh=($matching[0].online -eq $true)
        }
    }
    $link=Get-Private 'api/v1/mavlink/health'
    $report.pi_fresh_link_reported=($link.device_online -eq $true -and
                                     $link.fresh -eq $true)
    if ($VehicleId -and $link.vehicle_id -ne $VehicleId) {
        $report.pi_fresh_link_reported=$false
    }

    Write-Host '[5/6] Inspect cached parameters without requesting or writing them'
    if ($VehicleId -and $report.selected_aircraft_found) {
        $id=[Uri]::EscapeDataString($VehicleId)
        $parameters=Get-Private "api/v1/vehicles/$id/parameters" $headers
        $report.parameter_cache_verified_complete=(
            $parameters.verified_complete -eq $true -and
            $parameters.status -eq 'COMPLETE' -and
            $parameters.expected -gt 0 -and
            $parameters.count -eq $parameters.expected
        )
        if ($report.parameter_cache_verified_complete -and $parameters.captured_at) {
            $stamp=[DateTimeOffset]::Parse([string]$parameters.captured_at)
            $age=[DateTimeOffset]::UtcNow - $stamp
            $report.parameter_capture_recent=($age.TotalSeconds -ge 0 -and
                                               $age.TotalSeconds -le 300)
        }
    }

    Write-Host '[6/6] Evaluate strict optional bench gates'
    if ($RequireConnectedPi -and
        (-not $report.pi_fresh_link_reported -or
         -not $report.selected_aircraft_fresh)) {
        $report.errors += 'No verified fresh Pi/MAVLink and vehicle telemetry.'
        $failed=$true
    }
    if ($RequireCompleteParameters -and
        (-not $report.parameter_cache_verified_complete -or
         -not $report.parameter_capture_recent)) {
        $report.errors += 'Parameter cache incomplete or more than five minutes old.'
        $failed=$true
    }
    if (-not $VehicleId) {
        $report.errors += 'No unique registered aircraft; provide VehicleId when testing multiple drones.'
    }
}
catch {
    $failed=$true
    # Deliberately avoid embedding token, URL, vehicle ID, coordinates or HTTP
    # response body in output. Human sees local terminal error context only.
    $report.errors += 'Private cloud request or safety policy failed.'
    Write-Warning 'Read-only preflight failed; no aircraft command was issued.'
}
finally {
    $password=$null;$body=$null;$token=$null;$headers=$null;$credential=$null
    $dir=Join-Path $root 'build-logs'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $output=Join-Path $dir 'aerohelm-private-preflight.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $output -Encoding UTF8
    Write-Host "Sanitized private observation report: $output"
    Write-Host 'No flight authority and no parameter changes are granted by this test.'
}
if ($failed) { throw 'Preflight gates not met. Review the local report.' }
Write-Host 'Required read-only cloud checks passed; aircraft flight is not authorized.' -ForegroundColor Green
