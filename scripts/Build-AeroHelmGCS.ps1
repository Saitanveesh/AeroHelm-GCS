# AeroHelm GCS Windows build. Use Visual Studio's full-framework MSBuild.exe,
# not "dotnet build", for Mission Planner's legacy COM and WinForms resources.
# Works with Visual Studio / Build Tools 2022 and 2026 if the required
# .NET desktop components are installed.
#
# .\scripts\Build-AeroHelmGCS.ps1
# .\scripts\Build-AeroHelmGCS.ps1 -Target Plugin
# .\scripts\Build-AeroHelmGCS.ps1 -MSBuildPath 'C:\path\to\MSBuild.exe'
param(
    [ValidateSet("Solution", "Plugin")]
    [string] $Target = "Solution",
    [string] $MSBuildPath = ""
)
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Find-FrameworkMSBuild {
    param([string] $ExplicitPath)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "The specified MSBuild.exe does not exist: $ExplicitPath"
        }
        if ([IO.Path]::GetFileName($ExplicitPath) -ine "MSBuild.exe") {
            throw "-MSBuildPath must point to Visual Studio's MSBuild.exe, not dotnet.exe"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $programFilesX86 = [Environment]::GetFolderPath("ProgramFilesX86")
    $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        # No VS2022-only version filter: VS2026 (18.x), VS2022 (17.x),
        # Community, Professional, Enterprise and Build Tools are all eligible.
        # The Microsoft-supported -find option returns the full MSBuild.exe path.
        $findArguments = @("-products", "*", "-requires", "Microsoft.Component.MSBuild",
                           "-find", 'MSBuild\**\Bin\MSBuild.exe')
        $found = @(& $vswhere -latest @findArguments 2>$null)
        if (-not $found) {
            $found = @(& $vswhere -all @findArguments 2>$null)
        }
        foreach ($candidate in $found) {
            $candidate = ([string] $candidate).Trim()
            if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                return $candidate
            }
        }

        Write-Host "Visual Studio Installer was found, but no usable MSBuild.exe was detected." -ForegroundColor Yellow
        $installations = @(& $vswhere -all -products "*" -property installationPath 2>$null)
        if ($installations) {
            Write-Host "Detected Visual Studio installation directories:"
            foreach ($installation in $installations) {
                Write-Host " - $installation"
            }
            Write-Host "Open Visual Studio Installer -> Modify -> install '.NET desktop development' / '.NET desktop build tools'." -ForegroundColor Yellow
        } else {
            Write-Host "No completed Visual Studio / Build Tools installation was detected." -ForegroundColor Yellow
        }
    } else {
        Write-Host "Visual Studio Installer's vswhere.exe was not found." -ForegroundColor Yellow
    }

    # A custom developer command prompt may already put full-framework
    # MSBuild.exe on PATH. This must not be confused with dotnet build.
    $onPath = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source -and (Test-Path -LiteralPath $onPath.Source -PathType Leaf)) {
        return $onPath.Source
    }

    throw "Install/modify Visual Studio 2022/2026 or Build Tools with .NET desktop development, MSBuild and .NET Framework 4.7.2 targeting pack. Then rerun this script. 'dotnet --version' alone is insufficient."
}

$msbuild = Find-FrameworkMSBuild -ExplicitPath $MSBuildPath
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw "MissionPlanner.exe is running. Close it before rebuilding so plugin DLLs can be copied."
}
# Fast guard before expensive Visual Studio MSBuild. Past release snapshots
# under build-logs contained duplicate Plugins/*.cs. Never compile those.
if ($Target -eq "Solution") {
    $mainProject = [IO.File]::ReadAllText((Join-Path $repoRoot "MissionPlanner.csproj"))
    if (-not $mainProject.Contains('Compile Remove="build-logs\**"')) {
        throw 'Unsafe GCS project: backup plugin source files may be recompiled. Update MissionPlanner.csproj before building.'
    }
}
$project = if ($Target -eq "Plugin") {
    Join-Path $repoRoot "Plugins\AeroHelmCloud\AeroHelmCloud.Plugin.csproj"
} else {
    Join-Path $repoRoot "MissionPlanner.sln"
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Build target missing: $project"
}

# The build log is created only AFTER MSBuild discovery succeeds.
$logFolder = Join-Path $repoRoot "build-logs"
New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
$logPath = Join-Path $logFolder ("aerohelm-" + $Target.ToLowerInvariant() + ".log")

Write-Host "Using full-framework MSBuild: $msbuild"
Write-Host "Target: $project"
Write-Host "Build log: $logPath"
Write-Host "Serial build enabled to reduce concurrent plugin DLL copy conflicts."

# Record full diagnostic output on disk, not hundreds of duplicate lines
# in the operator's console. Build failures get a bounded first-error summary.
$consoleLog = Join-Path $logFolder ("aerohelm-" + $Target.ToLowerInvariant() + "-console.log")
Push-Location $repoRoot
try {
    & $msbuild $project /restore /m:1 /nr:false /p:Configuration=Release /verbosity:minimal /nologo "/flp:LogFile=$logPath;Verbosity=normal" /clp:ErrorsOnly *> $consoleLog
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($code -ne 0) {
    Write-Host "Build failed. First 12 errors; complete log: $logPath" -ForegroundColor Red
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        $errors = @(Select-String -LiteralPath $logPath -Pattern ': error (CS[0-9]+|MSB[0-9]+)' |
            Select-Object -First 12)
        foreach ($entry in $errors) { Write-Host $entry.Line }
    }
    Write-Host "MSBuild console output: $consoleLog"
    exit $code
}
Write-Host "Build succeeded. Review warnings in $logPath before releasing AeroHelm-GCS." -ForegroundColor Green
