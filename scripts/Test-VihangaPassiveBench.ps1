# Read-only Vihanga inventory. Windows PowerShell 5.1+.
# Never opens serial ports, sends MAVLink or changes flight controller state.
param(
    [string]$PiHost = '',
    [string]$CloudOrigin = '',
    [switch]$Export
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$serial=@([System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object -Unique)
$listeners=@(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
$owners=@()
foreach($listener in $listeners){
    $owners += [pscustomobject]@{
        bind_address=[string]$listener.LocalAddress
        port=[int]$listener.LocalPort
        pid=[int]$listener.OwningProcess
    }
}
$piReachable=$null
if(-not [string]::IsNullOrWhiteSpace($PiHost)){
    if($PiHost -match '[\\/\s@:]' -or
       [Uri]::CheckHostName($PiHost) -eq [UriHostNameType]::Unknown){
        throw 'PiHost must be a DNS name or IPv4 address without credentials or port.'
    }
    $piReachable=[bool](Test-NetConnection -ComputerName $PiHost -Port 22 -InformationLevel Quiet -WarningAction SilentlyContinue)
}
$cloudReachable=$null
if(-not [string]::IsNullOrWhiteSpace($CloudOrigin)){
    $uri=$null
    if(-not [Uri]::TryCreate($CloudOrigin,[UriKind]::Absolute,[ref]$uri) -or
       $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
       $uri.AbsolutePath -ne '/' -or
       -not (($uri.Scheme -eq 'https' -and
             ($uri.Host.EndsWith('.ts.net',[StringComparison]::OrdinalIgnoreCase) -or
              $uri.IsLoopback)) -or
            ($uri.Scheme -eq 'http' -and $uri.IsLoopback))){
        throw 'CloudOrigin must be private HTTPS on *.ts.net, or explicit loopback.'
    }
    $cloudReachable=[bool](Test-NetConnection -ComputerName $uri.DnsSafeHost -Port $uri.Port -InformationLevel Quiet -WarningAction SilentlyContinue)
}
$report=[ordered]@{
    schema='vihanga.passive-bench-inventory.v1'
    collected_utc=[DateTimeOffset]::UtcNow.ToString('o')
    serial_port_names=$serial
    local_tcp_5760_listeners=$owners
    pi_ssh_tcp_reachable=$piReachable
    cloud_tcp_reachable=$cloudReachable
    pi_identity_verified=$false
    cloud_tls_identity_and_auth_verified=$false
    mavlink_heartbeat_verified=$false
    autopilot_parameters_verified=$false
    vehicle_disarmed_verified=$false
    physical_flight_authorized=$false
}
Write-Host 'PASSIVE BENCH INVENTORY (not Pi/FC acceptance)'
Write-Host "Detected COM ports: $($serial -join ', ')"
if($listeners.Count -gt 0){
    Write-Warning "TCP 5760 has $($listeners.Count) listener(s); investigate ownership before starting the internal bridge."
}
if($null -ne $piReachable){
    Write-Host "Pi SSH TCP reachable: $piReachable (does not prove identity)"
}
if($null -ne $cloudReachable){
    Write-Host "Private cloud TCP reachable: $cloudReachable (does not prove TLS or login)"
}
if($Export){
    $out=Join-Path $root 'build-logs/vihanga-passive-bench.json'
    New-Item -Path (Split-Path -Parent $out) -ItemType Directory -Force | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $out -Encoding UTF8
    Write-Host "Sanitized report: $out"
}
Write-Host 'No serial port opened, no MAVLink command or parameter write sent.'
