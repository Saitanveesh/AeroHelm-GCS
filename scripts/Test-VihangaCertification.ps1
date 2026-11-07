# Vihanga Phase-3 operational acceptance assessment.
# Read-only inspection. Does not launch the GCS, authenticate, or command an aircraft.
# Hardware/flight certification requires independent signed acceptance evidence.
param(
    [switch]$RunSourceTests,
    [switch]$CheckGitHubCI,
    [string]$CloudReportPath='',
    [string]$OperatorEvidencePath='',
    [switch]$Export,
    [switch]$RequireSoftwareGate
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$approvedBranches=@('master','feat/vihanga-phase3-native-toolbar')
$branch=(& git -C $root branch --show-current).Trim()
$commit=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$'){
    throw 'Cannot identify an exact source commit for acceptance.'
}
$trackedChanges=@(& git -C $root status --porcelain --untracked-files=no)
if($LASTEXITCODE -ne 0){throw 'Cannot inspect Git checkout cleanliness.'}
$sourceRevision=($branch -in $approvedBranches -and $trackedChanges.Count -eq 0)
$warnings=New-Object 'System.Collections.Generic.List[string]'
if(-not $sourceRevision){
    $warnings.Add('Checkout is not the reviewed branch or contains tracked modifications.')
}
$sourceChecks=$false
if($RunSourceTests){
    try{
        & (Join-Path $PSScriptRoot 'Test-AeroHelmWindowsPages.ps1')
        if($LASTEXITCODE -ne 0){throw 'Windows source contracts returned a failure.'}
        & (Join-Path $PSScriptRoot 'Run-AeroHelmOfflineChecks.ps1')
        if($LASTEXITCODE -ne 0){throw 'Hardware-free unit tests returned a failure.'}
        $sourceChecks=$true
    }catch{
        $warnings.Add('Local source or protocol tests failed; inspect the terminal output.')
    }
}else{
    $warnings.Add('Offline tests not run in this assessment; use -RunSourceTests.')
}
$manifestFile=Join-Path $root 'build-logs/aerohelm-consolidated-release.json'
$runtime=Join-Path $root 'bin/Release/net461'
$exe=Join-Path $runtime 'MissionPlanner.exe'
$dll=Join-Path $runtime 'plugins/AeroHelm.Cloud.Plugin.Validation.dll'
$nativeHashes=$false
if((Test-Path -LiteralPath $manifestFile -PathType Leaf) -and
   (Test-Path -LiteralPath $exe -PathType Leaf) -and
   (Test-Path -LiteralPath $dll -PathType Leaf)){
    try{
        $manifest=Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
        $exeSha=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        $dllSha=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        $nativeHashes=($manifest.schema -eq 'aerohelm.gcs.consolidation.v1' -and
            $manifest.git_commit -eq $commit -and
            $manifest.source_branch -eq $branch -and
            $manifest.executable_sha256 -eq $exeSha -and
            $manifest.plugin_sha256 -eq $dllSha -and
            $manifest.native_build_reported_success -eq $true -and
            $manifest.plugin_installer_reported_success -eq $true)
    }catch{
        $warnings.Add('Local release manifest could not be verified.')
    }
}
if(-not $nativeHashes){
    $warnings.Add('Exact-commit native Windows EXE/DLL hashes are missing or do not match.')
}
$ci=$false
if($CheckGitHubCI){
    try{
        $url='https://api.github.com/repos/Saitanveesh/AeroHelm-GCS/actions/runs?head_sha='+$commit+'&per_page=100'
        $headers=@{'Accept'='application/vnd.github+json';'User-Agent'='VihangaReleaseAudit'}
        $runs=Invoke-RestMethod -Method Get -Uri $url -Headers $headers -MaximumRedirection 0 -TimeoutSec 15
        $good=@($runs.workflow_runs | Where-Object {
            $_.head_sha -eq $commit -and $_.status -eq 'completed' -and
            $_.conclusion -eq 'success' -and
            $_.name -eq 'AeroHelm Cloud Plugin Validation'
        })
        $ci=($good.Count -gt 0)
    }catch{
        $warnings.Add('GitHub Actions could not be verified; no CI pass inferred.')
    }
}
if(-not $ci){
    $warnings.Add('Passing complete GitHub Actions workflow for exact commit not confirmed.')
}
$cloud=$false
if(-not [string]::IsNullOrWhiteSpace($CloudReportPath)){
    try{
        $observations=Get-Content -LiteralPath $CloudReportPath -Raw | ConvertFrom-Json
        $timestamp=[DateTimeOffset]::Parse([string]$observations.time_utc)
        $age=[DateTimeOffset]::UtcNow-$timestamp
        $cloud=($observations.schema -eq 'aerohelm.private-preflight.v1' -and
            $observations.gcs_commit -eq $commit -and
            $age.TotalSeconds -ge 0 -and $age.TotalHours -le 24 -and
            $observations.cloud_healthy -eq $true -and
            $observations.authenticated -eq $true -and
            $observations.non_actuating_policy -eq $true -and
            $observations.audit_chain_verified_now -eq $true -and
            $observations.pi_fresh_link_reported -eq $true -and
            $observations.selected_aircraft_fresh -eq $true -and
            $observations.parameter_cache_verified_complete -eq $true -and
            $observations.parameter_capture_recent -eq $true -and
            @($observations.errors).Count -eq 0 -and
            $observations.physical_flight_authorized -eq $false)
    }catch{
        $warnings.Add('Private cloud preflight report is invalid or unreadable.')
    }
}
if(-not $cloud){
    $warnings.Add('Current, authenticated Pi/cloud/parameter preflight evidence is missing.')
}
$manual=$false
if(-not [string]::IsNullOrWhiteSpace($OperatorEvidencePath)){
    try{
        $evidence=Get-Content -LiteralPath $OperatorEvidencePath -Raw | ConvertFrom-Json
        $manual=($evidence.schema -eq 'vihanga.operator-acceptance.v1' -and
            $evidence.tested_commit -eq $commit -and
            $evidence.windows_gui_smoke -eq $true -and
            $evidence.radio_manual_selection -eq $true -and
            $evidence.cloud_outage_recovery -eq $true -and
            $evidence.simulator_ops -eq $true -and
            $evidence.intel_parameter_checks -eq $true -and
            $evidence.propeller_free_hil -eq $true -and
            $evidence.autopilot_failsafe_verified -eq $true -and
            -not [string]::IsNullOrWhiteSpace([string]$evidence.evidence_reference))
    }catch{
        $warnings.Add('Operator acceptance report is invalid or unreadable.')
    }
}
if(-not $manual){
    $warnings.Add('Independent Windows GUI and propeller-free hardware acceptance not documented.')
}
$software=($sourceRevision -and $sourceChecks -and $nativeHashes -and $ci -and $cloud)
$report=[ordered]@{
    schema='vihanga.certification-assessment.v1'
    created_utc=[DateTimeOffset]::UtcNow.ToString('o')
    gcs_commit=$commit
    reviewed_branch=$sourceRevision
    source_and_protocol_tests_passed=$sourceChecks
    exact_native_binary_hashes_verified=$nativeHashes
    exact_commit_github_ci_passed=$ci
    private_cloud_pi_preflight_passed=$cloud
    external_operator_evidence_present=$manual
    software_release_gate_passed=$software
    assessment_status=$(if($software -and $manual){'EXTERNAL SIGNOFF REQUIRED'}else{'NOT CLEARED'})
    operational_flight_certified=$false
    warnings=@($warnings.ToArray())
}
if($Export){
    $output=Join-Path $root 'build-logs/vihanga-certification-assessment.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $output -Encoding UTF8
    Write-Host "Sanitized assessment: $output"
}
Write-Host "Vihanga assessment: $($report.assessment_status)"
foreach($warning in $warnings){Write-Warning $warning}
Write-Host 'No aircraft commands were issued. Flight certification requires external signoff.'
if($RequireSoftwareGate -and -not $software){
    throw 'Software acceptance gates remain open. Do not deploy as certified.'
}
