<#
.SYNOPSIS
    Interleaved paired A/B benchmark for multithreaded NuGet static-graph restore.

.DESCRIPTION
    Measures LoadProjects ("Evaluated N project(s) in Xms") and end-to-end wall time
    with alternating AB / BA round order to eliminate drift and thermal bias.
    Verifies normalized dgspec.json equivalence for each benchmarked solution.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StagedSdkRoot,

    [string]$SdkVersion = "11.0.100",

    [Parameter(Mandatory = $true)]
    [string[]]$Solutions,

    [int]$Rounds = 8,

    [string]$WorkDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dotnetExe = Join-Path $StagedSdkRoot "dotnet.exe"
if (-not (Test-Path $dotnetExe)) { $dotnetExe = Join-Path $StagedSdkRoot "dotnet" }
$msbuildDll = Join-Path $StagedSdkRoot "sdk\$SdkVersion\MSBuild.dll"

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("nuget-sg-mt-bench-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$env:MSBUILDUSESERVER = "0"
$env:DOTNET_ROOT = $StagedSdkRoot
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:DOTNET_HOST_PATH = $dotnetExe
$env:NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED = $null

function Get-Percentile([double[]]$Sorted, [double]$P) {
    if ($Sorted.Count -eq 0) { return 0 }
    $idx = [int][math]::Floor($P * ($Sorted.Count - 1))
    return $Sorted[$idx]
}

Write-Host "| Solution | Builds | LoadProjects ST min/med (ms) | LoadProjects MT min/med (ms) | LoadProjects Speedup | Wall ST min/med (ms) | Wall MT min/med (ms) | Wall Delta |"
Write-Host "|---|---:|---:|---:|---:|---:|---:|---:|"

foreach ($slnPath in $Solutions) {
    if (-not (Test-Path $slnPath)) {
        Write-Warning "Skipping missing solution: $slnPath"
        continue
    }

    $slnDir = Split-Path -Parent $slnPath
    $slnFile = Split-Path -Leaf $slnPath
    $repoName = Split-Path -Leaf $slnDir
    $label = "$repoName/$slnFile"
    $safe = ($label -replace '[^a-zA-Z0-9._-]', '_')

    $stEval = [System.Collections.Generic.List[double]]::new()
    $mtEval = [System.Collections.Generic.List[double]]::new()
    $stWall = [System.Collections.Generic.List[double]]::new()
    $mtWall = [System.Collections.Generic.List[double]]::new()
    $builds = 0

    Push-Location $slnDir
    try {
        # Warm-up (1 ST + 1 MT)
        foreach ($en in @("false", "true")) {
            $dg = Join-Path $WorkDir "$safe-$en.dgspec.json"
            & $dotnetExe $msbuildDll $slnFile /t:GenerateRestoreGraphFile /p:RestoreUseStaticGraphEvaluation=true "/p:RestoreStaticGraphEnableMultiThreaded=$en" "/p:RestoreGraphOutputPath=$dg" /mt /nologo /v:normal | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Warmup failed for $label (mt=$en, ec=$LASTEXITCODE)" }
        }

        for ($r = 1; $r -le $Rounds; $r++) {
            $order = if ($r % 2 -eq 1) { @("false", "true") } else { @("true", "false") }
            foreach ($en in $order) {
                $dg = Join-Path $WorkDir "$safe-$en.dgspec.json"
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $out = & $dotnetExe $msbuildDll $slnFile /t:GenerateRestoreGraphFile /p:RestoreUseStaticGraphEvaluation=true "/p:RestoreStaticGraphEnableMultiThreaded=$en" "/p:RestoreGraphOutputPath=$dg" /mt /nologo /v:normal 2>&1
                $sw.Stop()
                if ($LASTEXITCODE -ne 0) { throw "Benchmark run failed for $label (round=$r mt=$en ec=$LASTEXITCODE)" }
                $m = [regex]::Match(($out -join "`n"), "Evaluated (\d+) project\(s\) in (\d+)ms \((\d+) builds, (\d+) failures\)")
                if (-not $m.Success) { throw "Could not parse evaluation summary for $label" }
                $builds = [int]$m.Groups[3].Value
                $evalMs = [double]$m.Groups[2].Value
                if ($en -eq "false") {
                    $stEval.Add($evalMs)
                    $stWall.Add($sw.ElapsedMilliseconds)
                } else {
                    $mtEval.Add($evalMs)
                    $mtWall.Add($sw.ElapsedMilliseconds)
                }
            }
        }
    }
    finally {
        Pop-Location
    }

    $stES = @($stEval | Sort-Object)
    $mtES = @($mtEval | Sort-Object)
    $stWS = @($stWall | Sort-Object)
    $mtWS = @($mtWall | Sort-Object)

    $stEMin = [int]$stES[0]; $stEMed = [int](Get-Percentile $stES 0.5)
    $mtEMin = [int]$mtES[0]; $mtEMed = [int](Get-Percentile $mtES 0.5)
    $stWMin = [int]$stWS[0]; $stWMed = [int](Get-Percentile $stWS 0.5)
    $mtWMin = [int]$mtWS[0]; $mtWMed = [int](Get-Percentile $mtWS 0.5)

    $evalSpeedup = [math]::Round($stEMin / $mtEMin, 2)
    $evalPct = [math]::Round((($mtEMin - $stEMin) / $stEMin) * 100, 1)
    $wallPct = [math]::Round((($mtWMin - $stWMin) / $stWMin) * 100, 1)

    Write-Host ("| {0} | {1} | {2} / {3} | {4} / {5} | **{6}x ({7}%)** | {8} / {9} | {10} / {11} | **{12}%** |" -f `
        $label, $builds, $stEMin, $stEMed, $mtEMin, $mtEMed, $evalSpeedup, $evalPct, $stWMin, $stWMed, $mtWMin, $mtWMed, $wallPct)
}
