<#
.SYNOPSIS
    Aggressive parity and edge-case integration test harness for multithreaded execution
    in NuGet static-graph restore (dotnet/msbuild#13701).

.DESCRIPTION
    Validates exact behavioral parity between single-threaded and multithreaded
    MSBuild static-graph restore across:
      1. 14 self-contained synthetic edge-case scenarios (multi-TFM, CPM, duplicate items,
         non-MT inline tasks with noisy stdout, non-MT compiled tasks calling BuildProjectFile,
         diamond nested <MSBuild> graphs, SDK unhappy paths, concurrent failures, opt-out
         precedence + option-injection guards, outer /mt in-proc execution, missing
         DOTNET_HOST_PATH, binlogs under strict mode, legacy PackageSpecFactory, and
         Desktop MSBuild.exe 18.14 MT + 18.8 version-gated fallback with packages.config).
      2. A real-world solution matrix covering large multi-project solutions on both
         .NET Core MSBuild and Desktop MSBuild.exe.

    Every scenario checks:
      - Exit code parity
      - Canonicalized dgspec.json (and project.assets.json where restored) exact parity
      - Diagnostic / warning parity
      - Zero lingering TaskHost (/nodemode:2) processes after exit
      - Binlog node concurrency verification where applicable
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StagedSdkRoot,

    [string]$SdkVersion = "11.0.100",

    [string]$Net472StageDir = "",

    [string]$DesktopMsBuild1814 = "C:\Program Files\Microsoft Visual Studio\18\IntPreview\MSBuild\Current\Bin\amd64\MSBuild.exe",

    [string]$DesktopMsBuild188 = "C:\Program Files\Microsoft Visual Studio\18\Main\MSBuild\Current\Bin\amd64\MSBuild.exe",

    [string]$WorkDir = "",

    [string[]]$RealSolutions = @(),

    [switch]$SkipSynthetic,

    [switch]$SkipRealSolutions,

    [string]$ReportPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dotnetExe = Join-Path $StagedSdkRoot "dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = Join-Path $StagedSdkRoot "dotnet"
}
$sdkDir = Join-Path $StagedSdkRoot "sdk\$SdkVersion"
$msbuildDll = Join-Path $sdkDir "MSBuild.dll"

if (-not (Test-Path $dotnetExe)) { throw "dotnet host not found at $dotnetExe" }
if (-not (Test-Path $msbuildDll)) { throw "MSBuild.dll not found at $msbuildDll" }

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("nuget-sg-mt-harness-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
}
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

# Helper C# tool compiled once for canonical JSON comparison + binlog node counting (no Python dependency).
$helperProjDir = Join-Path $WorkDir "_helper"
New-Item -ItemType Directory -Path $helperProjDir -Force | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.Build.Framework">
      <HintPath>$sdkDir\Microsoft.Build.Framework.dll</HintPath>
    </Reference>
    <Reference Include="Microsoft.Build">
      <HintPath>$sdkDir\Microsoft.Build.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $helperProjDir "HarnessHelper.csproj") -Encoding UTF8

@'
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: compare-json <a.json> <b.json> | inspect-binlog <file.binlog> | make-nupkg <dir> <id> <ver>");
    return 2;
}

switch (args[0])
{
    case "compare-json":
        return CompareJson(args[1], args[2]);
    case "inspect-binlog":
        return InspectBinlog(args[1]);
    case "make-nupkg":
        return MakeNupkg(args[1], args[2], args[3]);
    default:
        Console.Error.WriteLine($"Unknown command: {args[0]}");
        return 2;
}

static int CompareJson(string pathA, string pathB)
{
    using var docA = JsonDocument.Parse(File.ReadAllText(pathA));
    using var docB = JsonDocument.Parse(File.ReadAllText(pathB));
    string normA = CanonicalizeElement(docA.RootElement);
    string normB = CanonicalizeElement(docB.RootElement);

    if (string.Equals(normA, normB, StringComparison.Ordinal))
    {
        Console.WriteLine("MATCH");
        return 0;
    }

    var diffs = new List<string>();
    FindDiffs("$", docA.RootElement, docB.RootElement, diffs);
    Console.WriteLine($"DIFF ({diffs.Count} differences)");
    foreach (string d in diffs.Take(20))
    {
        Console.WriteLine("  " + d);
    }
    return 1;
}

static string CanonicalizeElement(JsonElement el)
{
    switch (el.ValueKind)
    {
        case JsonValueKind.Object:
            var props = el.EnumerateObject()
                .Select(p => (p.Name, Val: CanonicalizeElement(p.Value)))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Val, StringComparer.Ordinal);
            return "{" + string.Join(",", props.Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Val)) + "}";
        case JsonValueKind.Array:
            return "[" + string.Join(",", el.EnumerateArray().Select(CanonicalizeElement)) + "]";
        default:
            return el.GetRawText();
    }
}

static void FindDiffs(string path, JsonElement a, JsonElement b, List<string> diffs)
{
    if (diffs.Count >= 25) return;
    if (a.ValueKind != b.ValueKind)
    {
        diffs.Add($"{path}: kind {a.ValueKind} != {b.ValueKind}");
        return;
    }
    if (a.ValueKind == JsonValueKind.Object)
    {
        var mapA = a.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Value).ToList(), StringComparer.Ordinal);
        var mapB = b.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Value).ToList(), StringComparer.Ordinal);
        foreach (string k in mapA.Keys.Union(mapB.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!mapA.TryGetValue(k, out var la)) { diffs.Add($"{path}.{k}: missing in A"); continue; }
            if (!mapB.TryGetValue(k, out var lb)) { diffs.Add($"{path}.{k}: missing in B"); continue; }
            if (la.Count != lb.Count) { diffs.Add($"{path}.{k}: duplicate key count {la.Count} != {lb.Count}"); continue; }
            var sa = la.Select(CanonicalizeElement).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sb = lb.Select(CanonicalizeElement).OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int i = 0; i < sa.Count; i++)
            {
                if (!string.Equals(sa[i], sb[i], StringComparison.Ordinal))
                {
                    if (la.Count == 1) FindDiffs($"{path}.{k}", la[0], lb[0], diffs);
                    else diffs.Add($"{path}.{k}[{i}]: differs");
                }
            }
        }
        return;
    }
    if (a.ValueKind == JsonValueKind.Array)
    {
        int lenA = a.GetArrayLength(), lenB = b.GetArrayLength();
        if (lenA != lenB) { diffs.Add($"{path}: array len {lenA} != {lenB}"); return; }
        for (int i = 0; i < lenA; i++) FindDiffs($"{path}[{i}]", a[i], b[i], diffs);
        return;
    }
    if (!string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal))
    {
        diffs.Add($"{path}: {a.GetRawText()} != {b.GetRawText()}");
    }
}

static int InspectBinlog(string binlogPath)
{
    var reader = new BinaryLogReplayEventSource();
    var projectNodes = new HashSet<int>();
    var taskHostMessages = new List<string>();
    int errors = 0;
    int warnings = 0;
    int projectStarted = 0;

    reader.ProjectStarted += (_, e) =>
    {
        projectStarted++;
        if (e.BuildEventContext != null && e.BuildEventContext.NodeId > 0)
        {
            projectNodes.Add(e.BuildEventContext.NodeId);
        }
    };
    reader.MessageRaised += (_, e) =>
    {
        if (e.Message != null && (e.Message.Contains("TaskHost", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("external task host", StringComparison.OrdinalIgnoreCase)))
        {
            taskHostMessages.Add(e.Message);
        }
    };
    reader.WarningRaised += (_, _) => warnings++;
    reader.ErrorRaised += (_, _) => errors++;

    reader.Replay(binlogPath);
    Console.WriteLine($"NODES={projectNodes.Count};NODE_IDS={string.Join(",", projectNodes.OrderBy(x => x))};PROJECTS={projectStarted};TASKHOST_MSGS={taskHostMessages.Count};WARNINGS={warnings};ERRORS={errors}");
    return 0;
}

static int MakeNupkg(string feedDir, string id, string version)
{
    Directory.CreateDirectory(feedDir);
    string nupkgPath = Path.Combine(feedDir, $"{id}.{version}.nupkg");
    if (File.Exists(nupkgPath)) File.Delete(nupkgPath);
    using var zip = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);

    var nuspecEntry = zip.CreateEntry($"{id}.nuspec");
    using (var w = new StreamWriter(nuspecEntry.Open(), new UTF8Encoding(false)))
    {
        w.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>Harness</authors>
                <description>Test package {id}</description>
              </metadata>
            </package>
            """);
    }

    foreach (string tfm in new[] { "netstandard2.0", "net8.0", "net9.0", "net10.0", "net11.0", "net472" })
    {
        var placeholder = zip.CreateEntry($"lib/{tfm}/_._");
        using var s = placeholder.Open();
    }
    return 0;
}
'@ | Set-Content (Join-Path $helperProjDir "Program.cs") -Encoding UTF8

# Build helper once (using clean env)
$env:MSBUILDUSESERVER = "0"
$env:DOTNET_ROOT = $StagedSdkRoot
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:DOTNET_HOST_PATH = $dotnetExe

Push-Location $helperProjDir
& $dotnetExe build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build HarnessHelper" }
Pop-Location
$helperDll = Join-Path $helperProjDir "bin\Release\net11.0\HarnessHelper.dll"

# Also build a compiled non-MT custom task assembly (for scenario 5: IBuildEngine.BuildProjectFile from TaskHost)
$customTaskDir = Join-Path $WorkDir "_customtask"
New-Item -ItemType Directory -Path $customTaskDir -Force | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.Build.Framework">
      <HintPath>$sdkDir\Microsoft.Build.Framework.dll</HintPath>
    </Reference>
    <Reference Include="Microsoft.Build.Utilities.Core">
      <HintPath>$sdkDir\Microsoft.Build.Utilities.Core.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $customTaskDir "NonMtCallbackTask.csproj") -Encoding UTF8

@'
using System.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace HarnessTasks
{
    // Intentionally NOT marked [MSBuildMultiThreadableTask] so MSBuild routes it to a TaskHost under /mt.
    public sealed class NonMtBuildCallbackTask : Task
    {
        [Required]
        public string ProjectPath { get; set; } = "";

        [Required]
        public string TargetName { get; set; } = "";

        [Output]
        public string ResultValue { get; set; } = "";

        public override bool Execute()
        {
            var outputs = new Hashtable();
            if (!BuildEngine.BuildProjectFile(ProjectPath, new[] { TargetName }, globalProperties: null, targetOutputs: outputs))
            {
                return false;
            }
            if (outputs[TargetName] is ITaskItem[] items && items.Length > 0)
            {
                ResultValue = items[0].ItemSpec;
            }
            return true;
        }
    }
}
'@ | Set-Content (Join-Path $customTaskDir "NonMtBuildCallbackTask.cs") -Encoding UTF8

Push-Location $customTaskDir
& $dotnetExe build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build NonMtCallbackTask" }
Pop-Location
$customTaskDll = Join-Path $customTaskDir "bin\Release\net11.0\NonMtCallbackTask.dll"

function Invoke-Helper([string[]]$HelperArgs) {
    $out = & $dotnetExe exec $helperDll @HelperArgs 2>&1
    return [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n").Trim() }
}

function Get-TaskHostCount {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        return @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='MSBuild.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like "*nodemode:2*" }).Count
    }
    return 0
}

function Invoke-CoreMsBuild([string]$WorkingDir, [string[]]$MsBuildArgs, [hashtable]$ExtraEnv = @{}, [switch]$UnsetDotnetHostPath) {
    $savedEnv = @{}
    $keysToManage = @("NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED", "NUGET_USE_NEW_PACKAGESPEC_FACTORY", "RESTORE_TASK_BINLOG_PARAMETERS", "DOTNET_HOST_PATH", "MSBUILDUSESERVER", "DOTNET_ROOT", "DOTNET_MULTILEVEL_LOOKUP")
    foreach ($k in $keysToManage) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k) }
    foreach ($k in $ExtraEnv.Keys) {
        if (-not $savedEnv.ContainsKey($k)) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k) }
    }

    try {
        [Environment]::SetEnvironmentVariable("MSBUILDUSESERVER", "0")
        [Environment]::SetEnvironmentVariable("DOTNET_ROOT", $StagedSdkRoot)
        [Environment]::SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0")
        [Environment]::SetEnvironmentVariable("NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED", $null)
        [Environment]::SetEnvironmentVariable("NUGET_USE_NEW_PACKAGESPEC_FACTORY", $null)
        [Environment]::SetEnvironmentVariable("RESTORE_TASK_BINLOG_PARAMETERS", $null)
        if ($UnsetDotnetHostPath) {
            [Environment]::SetEnvironmentVariable("DOTNET_HOST_PATH", $null)
        } else {
            [Environment]::SetEnvironmentVariable("DOTNET_HOST_PATH", $dotnetExe)
        }
        foreach ($kv in $ExtraEnv.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value)
        }

        Push-Location $WorkingDir
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $out = & $dotnetExe $msbuildDll @MsBuildArgs 2>&1
        $ec = $LASTEXITCODE
        $sw.Stop()
        Pop-Location

        $text = ($out -join "`n")
        $m = [regex]::Match($text, "Evaluated (\d+) project\(s\) in (\d+)ms \((\d+) builds, (\d+) failures\)")
        $evalMs = if ($m.Success) { [int]$m.Groups[2].Value } else { -1 }
        $projCount = if ($m.Success) { [int]$m.Groups[1].Value } else { -1 }
        $buildCount = if ($m.Success) { [int]$m.Groups[3].Value } else { -1 }
        return [PSCustomObject]@{
            ExitCode   = $ec
            WallMs     = $sw.ElapsedMilliseconds
            EvalMs     = $evalMs
            ProjCount  = $projCount
            BuildCount = $buildCount
            Output     = $text
        }
    }
    finally {
        foreach ($kv in $savedEnv.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value)
        }
    }
}

function Invoke-DesktopMsBuild([string]$MsBuildExe, [string]$WorkingDir, [string[]]$MsBuildArgs, [hashtable]$ExtraEnv = @{}) {
    $savedEnv = @{}
    $keysToManage = @("NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED", "NUGET_USE_NEW_PACKAGESPEC_FACTORY", "RESTORE_TASK_BINLOG_PARAMETERS", "MSBUILDUSESERVER")
    foreach ($k in $keysToManage) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k) }
    foreach ($k in $ExtraEnv.Keys) {
        if (-not $savedEnv.ContainsKey($k)) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k) }
    }

    try {
        [Environment]::SetEnvironmentVariable("MSBUILDUSESERVER", "0")
        [Environment]::SetEnvironmentVariable("NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED", $null)
        [Environment]::SetEnvironmentVariable("NUGET_USE_NEW_PACKAGESPEC_FACTORY", $null)
        [Environment]::SetEnvironmentVariable("RESTORE_TASK_BINLOG_PARAMETERS", $null)
        foreach ($kv in $ExtraEnv.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value)
        }

        $baseArgs = @(
            "/p:NuGetRestoreTargets=$Net472StageDir\NuGet.targets",
            "/p:RestoreTaskAssemblyFile=$Net472StageDir\NuGet.Build.Tasks.dll"
        ) + $MsBuildArgs

        Push-Location $WorkingDir
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $out = & $MsBuildExe @baseArgs 2>&1
        $ec = $LASTEXITCODE
        $sw.Stop()
        Pop-Location

        $text = ($out -join "`n")
        $m = [regex]::Match($text, "Evaluated (\d+) project\(s\) in (\d+)ms \((\d+) builds, (\d+) failures\)")
        $evalMs = if ($m.Success) { [int]$m.Groups[2].Value } else { -1 }
        $projCount = if ($m.Success) { [int]$m.Groups[1].Value } else { -1 }
        return [PSCustomObject]@{
            ExitCode  = $ec
            WallMs    = $sw.ElapsedMilliseconds
            EvalMs    = $evalMs
            ProjCount = $projCount
            Output    = $text
        }
    }
    finally {
        foreach ($kv in $savedEnv.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value)
        }
    }
}

$results = [System.Collections.Generic.List[object]]::new()

function Record-Result([string]$Category, [string]$Name, [bool]$Passed, [string]$Details) {
    $status = if ($Passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] ({1}) {2} -- {3}" -f $status, $Category, $Name, $Details) -ForegroundColor $(if ($Passed) { "Green" } else { "Red" })
    $results.Add([PSCustomObject]@{
        Category = $Category
        Name     = $Name
        Passed   = $Passed
        Details  = $Details
    })
}

# Local package feed + isolated NuGet.config for offline synthetic tests
$feedDir = Join-Path $WorkDir "_feed"
$packagesDir = Join-Path $WorkDir "_packages"
foreach ($pkg in @(
    @("Pkg.Alpha", "1.0.0"), @("Pkg.Beta", "2.0.0"), @("Pkg.Gamma", "3.0.0"),
    @("Pkg.Global", "1.2.3"), @("Pkg.Download", "1.0.0"), @("Pkg.Dup", "1.0.0"),
    @("Pkg.Dup", "2.0.0"), @("Pkg.Legacy", "1.0.0"), @("Pkg.NetFx", "1.0.0")
)) {
    $null = Invoke-Helper @("make-nupkg", $feedDir, $pkg[0], $pkg[1])
}

function Write-IsolatedNuGetConfig([string]$Dir) {
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feedDir" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $Dir "NuGet.config") -Encoding UTF8
}

# ==============================================================================
# PART A: SYNTHETIC EDGE-CASE MATRIX (14 SCENARIOS)
# ==============================================================================
if (-not $SkipSynthetic) {
    Write-Host "`n=== PART A: Synthetic Edge-Case Parity Matrix ===" -ForegroundColor Cyan

    # --- Scenario 1: 16-project x 3-TFM multi-targeting + full restore assets parity ---
    try {
        $sDir = Join-Path $WorkDir "s01-multitfm"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $slnProjs = @()
        for ($i = 1; $i -le 16; $i++) {
            $pDir = Join-Path $sDir "P$i"
            New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            $refXml = if ($i -gt 1) { "<ProjectReference Include=`"..\P$($i-1)\P$($i-1).csproj`" Condition=`"'`$(TargetFramework)' != 'net8.0'`" />" } else { "" }
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0;net11.0</TargetFrameworks>
    <NuGetAudit>false</NuGetAudit>
    <RestorePackagesPath>$packagesDir</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Pkg.Alpha" Version="1.0.0" />
    <PackageReference Include="Pkg.Beta" Version="2.0.0" Condition="'`$(TargetFramework)' == 'net11.0'" />
    <PackageDownload Include="Pkg.Download" Version="[1.0.0]" Condition="'`$(TargetFramework)' == 'net10.0'" />
    $refXml
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $pDir "P$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"P$i\P$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "MultiTfm.slnx") -Encoding UTF8

        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $blMt = Join-Path $sDir "mt.binlog"
        $rSt = Invoke-CoreMsBuild $sDir @("MultiTfm.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("MultiTfm.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal") @{ RESTORE_TASK_BINLOG_PARAMETERS = "LogFile=$blMt" }
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $blInfo = Invoke-Helper @("inspect-binlog", $blMt)

        # Full restore assets parity (standard vs SG-ST vs SG-MT)
        $rStd = Invoke-CoreMsBuild $sDir @("MultiTfm.slnx", "/t:Restore", "/p:RestoreForce=true", "/p:RestoreUseStaticGraphEvaluation=false", "/nologo", "/v:minimal")
        Copy-Item (Join-Path $sDir "P16\obj\project.assets.json") (Join-Path $sDir "assets-std.json") -Force
        $rSgMt = Invoke-CoreMsBuild $sDir @("MultiTfm.slnx", "/t:Restore", "/p:RestoreForce=true", "/p:RestoreUseStaticGraphEvaluation=true", "/nologo", "/v:minimal")
        Copy-Item (Join-Path $sDir "P16\obj\project.assets.json") (Join-Path $sDir "assets-sgmt.json") -Force
        $cmpAssets = Invoke-Helper @("compare-json", (Join-Path $sDir "assets-std.json"), (Join-Path $sDir "assets-sgmt.json"))

        $multiNode = ($blInfo.Output -match "NODES=(\d+)") -and ([int]$Matches[1] -gt 1)
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($rStd.ExitCode -eq 0) -and ($rSgMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and ($cmpAssets.ExitCode -eq 0) -and $multiNode
        Record-Result "Synthetic" "01-MultiTfm-64Builds-DgSpecAndAssetsParity" $ok "dgspec=$($cmp.Output) assets=$($cmpAssets.Output) $($blInfo.Output)"
    } catch { Record-Result "Synthetic" "01-MultiTfm-64Builds-DgSpecAndAssetsParity" $false $_.Exception.Message }

    # --- Scenario 2: Central Package Management + GlobalPackageReference + Transitive Pinning ---
    try {
        $sDir = Join-Path $WorkDir "s02-cpm"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        @"
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Pkg.Alpha" Version="1.0.0" />
    <PackageVersion Include="Pkg.Beta" Version="2.0.0" />
    <GlobalPackageReference Include="Pkg.Global" Version="1.2.3" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $sDir "Directory.Packages.props") -Encoding UTF8
        $slnProjs = @()
        for ($i = 1; $i -le 12; $i++) {
            $pDir = Join-Path $sDir "C$i"
            New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Pkg.Alpha" />
    <PackageReference Include="Pkg.Beta" VersionOverride="2.0.0" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $pDir "C$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"C$i\C$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "Cpm.slnx") -Encoding UTF8

        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("Cpm.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("Cpm.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal")
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0)
        Record-Result "Synthetic" "02-CPM-GlobalPkgRef-TransitivePinning" $ok "dgspec=$($cmp.Output)"
    } catch { Record-Result "Synthetic" "02-CPM-GlobalPkgRef-TransitivePinning" $false $_.Exception.Message }

    # --- Scenario 3: Duplicate NuGet items (NU1504/NU1505/NU1508) under concurrency ---
    try {
        $sDir = Join-Path $WorkDir "s03-dups"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $slnProjs = @()
        for ($i = 1; $i -le 10; $i++) {
            $pDir = Join-Path $sDir "D$i"
            New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Pkg.Dup" Version="1.0.0" />
    <PackageReference Include="Pkg.Dup" Version="2.0.0" />
    <PackageDownload Include="Pkg.Download" Version="[1.0.0]" />
    <PackageDownload Include="Pkg.Download" Version="[1.0.0]" />
    <NuGetAuditSuppress Include="https://example.com/advisory/1" />
    <NuGetAuditSuppress Include="https://example.com/advisory/1" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $pDir "D$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"D$i\D$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "Dups.slnx") -Encoding UTF8
        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("Dups.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("Dups.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal")
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $stW = [regex]::Matches($rSt.Output, "warning NU150[458]:").Count
        $mtW = [regex]::Matches($rMt.Output, "warning NU150[458]:").Count
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and ($stW -eq $mtW) -and ($stW -gt 0)
        Record-Result "Synthetic" "03-DuplicateNuGetItems-ConcurrentWarnings" $ok "dgspec=$($cmp.Output) warnings ST=$stW MT=$mtW"
    } catch { Record-Result "Synthetic" "03-DuplicateNuGetItems-ConcurrentWarnings" $false $_.Exception.Message }

    # --- Scenario 4: Non-MT inline task writing noisy partial Console.Write + CWD check + TaskHost cleanup ---
    try {
        $sDir = Join-Path $WorkDir "s04-nonmt-stdout"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $slnProjs = @()
        for ($i = 1; $i -le 8; $i++) {
            $pDir = Join-Path $sDir "N$i"
            New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <UsingTask TaskName="NoisyInlineTask" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="`$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll">
    <ParameterGroup>
      <Id ParameterType="System.String" Required="true" />
      <ExpectedDir ParameterType="System.String" Required="true" />
      <PkgOut ParameterType="System.String" Output="true" />
    </ParameterGroup>
    <Task>
      <Code Type="Fragment" Language="cs"><![CDATA[
        System.Console.Write("PARTIAL_NOISE_" + Id + "_");
        string cwd = System.IO.Directory.GetCurrentDirectory();
        if (!string.Equals(cwd.TrimEnd('\\', '/'), ExpectedDir.TrimEnd('\\', '/'), System.StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError("CWD_MISMATCH expected=" + ExpectedDir + " actual=" + cwd);
            return false;
        }
        Log.LogWarning("NOISY_WARN_" + Id);
        PkgOut = "Pkg.Alpha";
      ]]></Code>
    </Task>
  </UsingTask>
  <Target Name="AddDyn" BeforeTargets="CollectPackageReferences">
    <NoisyInlineTask Id="N$i" ExpectedDir="`$(MSBuildProjectDirectory)">
      <Output TaskParameter="PkgOut" PropertyName="_Dyn" />
    </NoisyInlineTask>
    <ItemGroup>
      <PackageReference Include="`$(_Dyn)" Version="1.0.0" />
    </ItemGroup>
  </Target>
</Project>
"@ | Set-Content (Join-Path $pDir "N$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"N$i\N$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "Noisy.slnx") -Encoding UTF8
        $thBefore = Get-TaskHostCount
        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("Noisy.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("Noisy.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal")
        Start-Sleep -Milliseconds 300
        $thAfter = Get-TaskHostCount
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $mtWarnCount = [regex]::Matches($rMt.Output, "warning : NOISY_WARN_N\d").Count
        $stWarnCount = [regex]::Matches($rSt.Output, "warning : NOISY_WARN_N\d").Count
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and ($mtWarnCount -eq $stWarnCount) -and ($mtWarnCount -ge 8) -and ($thAfter -le $thBefore)
        Record-Result "Synthetic" "04-NonMtInlineTask-StdoutIsolation-CWD-NoLeaks" $ok "dgspec=$($cmp.Output) warns ST=$stWarnCount MT=$mtWarnCount th=$thBefore->$thAfter"
    } catch { Record-Result "Synthetic" "04-NonMtInlineTask-StdoutIsolation-CWD-NoLeaks" $false $_.Exception.Message }

    # --- Scenario 5: Non-MT compiled assembly task calling IBuildEngine.BuildProjectFile from TaskHost ---
    try {
        $sDir = Join-Path $WorkDir "s05-nonmt-callback"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $libDir = Join-Path $sDir "Lib"; New-Item -ItemType Directory -Path $libDir -Force | Out-Null
        $appDir = Join-Path $sDir "App"; New-Item -ItemType Directory -Path $appDir -Force | Out-Null
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <Target Name="ProvidePkgName" Returns="Pkg.Beta" />
</Project>
"@ | Set-Content (Join-Path $libDir "Lib.csproj") -Encoding UTF8
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <UsingTask TaskName="HarnessTasks.NonMtBuildCallbackTask" AssemblyFile="$customTaskDll" />
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
  <Target Name="FetchFromLib" BeforeTargets="CollectPackageReferences">
    <NonMtBuildCallbackTask ProjectPath="$libDir\Lib.csproj" TargetName="ProvidePkgName">
      <Output TaskParameter="ResultValue" PropertyName="_FetchedPkg" />
    </NonMtBuildCallbackTask>
    <ItemGroup><PackageReference Include="`$(_FetchedPkg)" Version="2.0.0" /></ItemGroup>
  </Target>
</Project>
"@ | Set-Content (Join-Path $appDir "App.csproj") -Encoding UTF8
        "<Solution>`n  <Project Path=`"Lib\Lib.csproj`" />`n  <Project Path=`"App\App.csproj`" />`n</Solution>" | Set-Content (Join-Path $sDir "Callback.slnx") -Encoding UTF8

        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("Callback.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("Callback.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal")
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $hasPkg = (Test-Path $dgMt) -and ((Get-Content $dgMt -Raw) -match "Pkg\.Beta")
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and $hasPkg
        Record-Result "Synthetic" "05-NonMtCompiledTask-BuildProjectFileCallback" $ok "dgspec=$($cmp.Output) hasPkg=$hasPkg"
    } catch { Record-Result "Synthetic" "05-NonMtCompiledTask-BuildProjectFileCallback" $false $_.Exception.Message }

    # --- Scenario 6: Diamond nested <MSBuild> dependency graph ---
    try {
        $sDir = Join-Path $WorkDir "s06-diamond"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        foreach ($n in @("A", "B", "C", "D")) { New-Item -ItemType Directory -Path (Join-Path $sDir $n) -Force | Out-Null }
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <Target Name="GetInfo" Returns="FromD">
    <PropertyGroup><_DProp>ComputedD</_DProp></PropertyGroup>
  </Target>
  <Target Name="AddDPkg" BeforeTargets="CollectPackageReferences" DependsOnTargets="GetInfo">
    <ItemGroup><PackageReference Include="Pkg.`$(_DProp)" Version="1.0.0" /></ItemGroup>
  </Target>
</Project>
"@ | Set-Content (Join-Path $sDir "D\D.csproj") -Encoding UTF8
        foreach ($n in @("B", "C")) {
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup><ProjectReference Include="..\D\D.csproj" /></ItemGroup>
  <Target Name="QueryD" BeforeTargets="CollectPackageReferences">
    <MSBuild Projects="@(ProjectReference)" Targets="GetInfo">
      <Output TaskParameter="TargetOutputs" PropertyName="_DOut" />
    </MSBuild>
    <ItemGroup><PackageReference Include="Pkg.$n.`$(_DOut)" Version="1.0.0" /></ItemGroup>
  </Target>
</Project>
"@ | Set-Content (Join-Path $sDir "$n\$n.csproj") -Encoding UTF8
        }
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\B\B.csproj" />
    <ProjectReference Include="..\C\C.csproj" />
  </ItemGroup>
  <Target Name="QueryBC" BeforeTargets="CollectPackageReferences">
    <MSBuild Projects="..\D\D.csproj" Targets="GetInfo">
      <Output TaskParameter="TargetOutputs" PropertyName="_DOut" />
    </MSBuild>
    <ItemGroup><PackageReference Include="Pkg.A.`$(_DOut)" Version="1.0.0" /></ItemGroup>
  </Target>
</Project>
"@ | Set-Content (Join-Path $sDir "A\A.csproj") -Encoding UTF8
        "<Solution>`n  <Project Path=`"A\A.csproj`" />`n  <Project Path=`"B\B.csproj`" />`n  <Project Path=`"C\C.csproj`" />`n  <Project Path=`"D\D.csproj`" />`n</Solution>" | Set-Content (Join-Path $sDir "Diamond.slnx") -Encoding UTF8

        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("Diamond.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("Diamond.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal")
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $raw = if (Test-Path $dgMt) { Get-Content $dgMt -Raw } else { "" }
        $allPresent = ($raw -match "Pkg\.ComputedD") -and ($raw -match "Pkg\.B\.FromD") -and ($raw -match "Pkg\.C\.FromD") -and ($raw -match "Pkg\.A\.FromD")
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and $allPresent
        Record-Result "Synthetic" "06-DiamondNestedMSBuildCalls" $ok "dgspec=$($cmp.Output) allPresent=$allPresent"
    } catch { Record-Result "Synthetic" "06-DiamondNestedMSBuildCalls" $false $_.Exception.Message }

    # --- Scenario 7: SDK unhappy path (NETSdkWarning via TargetFrameworkIsNotRecommended on netstandard1.6) ---
    try {
        $sDir = Join-Path $WorkDir "s07-sdk-warn"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $slnProjs = @()
        for ($i = 1; $i -le 4; $i++) {
            $pDir = Join-Path $sDir "W$i"; New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>netstandard1.6</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Pkg.Alpha" Version="1.0.0" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $pDir "W$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"W$i\W$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "SdkWarn.slnx") -Encoding UTF8
        $dgSt = Join-Path $sDir "st.dgspec.json"
        $dgMt = Join-Path $sDir "mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("SdkWarn.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("SdkWarn.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal")
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $stW = [regex]::Matches($rSt.Output, "warning NETSDK\d+:").Count
        $mtW = [regex]::Matches($rMt.Output, "warning NETSDK\d+:").Count
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and ($stW -eq $mtW) -and ($stW -gt 0)
        Record-Result "Synthetic" "07-SdkUnhappyPath-NETSdkWarning" $ok "dgspec=$($cmp.Output) NETSDK warns ST=$stW MT=$mtW"
    } catch { Record-Result "Synthetic" "07-SdkUnhappyPath-NETSdkWarning" $false $_.Exception.Message }

    # --- Scenario 8: Concurrent target failure among 8 projects ---
    try {
        $sDir = Join-Path $WorkDir "s08-failure"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $slnProjs = @()
        for ($i = 1; $i -le 8; $i++) {
            $pDir = Join-Path $sDir "F$i"; New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            $failTarget = if ($i -eq 5) { '<Target Name="Boom" BeforeTargets="CollectPackageReferences"><Error Text="DELIBERATE_FAIL_F5" /></Target>' } else { '' }
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  $failTarget
</Project>
"@ | Set-Content (Join-Path $pDir "F$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"F$i\F$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "Fail.slnx") -Encoding UTF8
        $thBefore = Get-TaskHostCount
        $rSt = Invoke-CoreMsBuild $sDir @("Fail.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$sDir\st.dgspec.json", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $sDir @("Fail.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$sDir\mt.dgspec.json", "/nologo", "/v:normal")
        $thAfter = Get-TaskHostCount
        $ok = ($rSt.ExitCode -ne 0) -and ($rMt.ExitCode -ne 0) -and ($rMt.Output -match "DELIBERATE_FAIL_F5") -and ($thAfter -le $thBefore)
        Record-Result "Synthetic" "08-ConcurrentTargetFailure-CleanErrorAndCleanup" $ok "exit ST=$($rSt.ExitCode) MT=$($rMt.ExitCode) th=$thBefore->$thAfter"
    } catch { Record-Result "Synthetic" "08-ConcurrentTargetFailure-CleanErrorAndCleanup" $false $_.Exception.Message }

    # --- Scenario 9: Opt-out, Single-Project Gate, Precedence & Option Injection Guard ---
    try {
        $sDir = Join-Path $WorkDir "s09-optout"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $slnProjs = @()
        for ($i = 1; $i -le 6; $i++) {
            $pDir = Join-Path $sDir "O$i"; New-Item -ItemType Directory -Path $pDir -Force | Out-Null
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>' | Set-Content (Join-Path $pDir "O$i.csproj") -Encoding UTF8
            $slnProjs += "  <Project Path=`"O$i\O$i.csproj`" />"
        }
        "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "OptOut.slnx") -Encoding UTF8

        $cases = @(
            @{ Label = "DefaultMultiProj"; Target = "OptOut.slnx"; Extra = @(); Env = @{}; ExpectMt = $true },
            @{ Label = "SingleProject"; Target = "O1\O1.csproj"; Extra = @(); Env = @{}; ExpectMt = $false },
            @{ Label = "PropFalse"; Target = "OptOut.slnx"; Extra = @("/p:RestoreStaticGraphEnableMultiThreaded=false"); Env = @{}; ExpectMt = $false },
            @{ Label = "DisableParallel"; Target = "OptOut.slnx"; Extra = @("/p:RestoreDisableParallel=true"); Env = @{}; ExpectMt = $false },
            @{ Label = "EnvFalseOverridesPropTrue"; Target = "OptOut.slnx"; Extra = @("/p:RestoreStaticGraphEnableMultiThreaded=true"); Env = @{ NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED = "false" }; ExpectMt = $false },
            @{ Label = "EnvZero"; Target = "OptOut.slnx"; Extra = @(); Env = @{ NUGET_RESTORE_STATIC_GRAPH_MULTITHREADED = "0" }; ExpectMt = $false },
            @{ Label = "InjectionIgnored"; Target = "OptOut.slnx"; Extra = @("/p:RestoreStaticGraphEnableMultiThreaded=banana;Recursive=False"); Env = @{}; ExpectMt = $true }
        )

        $allOk = $true
        $notes = @()
        foreach ($c in $cases) {
            $bl = Join-Path $sDir "$($c.Label).binlog"
            $dg = Join-Path $sDir "$($c.Label).dgspec.json"
            $envMap = @{} + $c.Env
            $envMap["RESTORE_TASK_BINLOG_PARAMETERS"] = "LogFile=$bl"
            $res = Invoke-CoreMsBuild $sDir (@($c.Target, "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dg", "/nologo", "/v:normal") + $c.Extra) $envMap
            $info = Invoke-Helper @("inspect-binlog", $bl)
            $isMt = ($info.Output -match "NODE_IDS=([0-9,]+)") -and ($Matches[1] -ne "1") -and ($Matches[1] -ne "")
            $caseOk = ($res.ExitCode -eq 0) -and ($isMt -eq $c.ExpectMt)
            if (-not $caseOk) { $allOk = $false }
            $notes += "$($c.Label):ok=$caseOk(mt=$isMt)"
        }
        Record-Result "Synthetic" "09-OptOut-SingleProjGate-Precedence-InjectionGuard" $allOk ($notes -join "; ")
    } catch { Record-Result "Synthetic" "09-OptOut-SingleProjGate-Precedence-InjectionGuard" $false $_.Exception.Message }

    # --- Scenario 10: Outer /mt runs RestoreTaskEx & GenerateRestoreGraphFileTask in-proc (0 TaskHosts) + relative paths ---
    try {
        $sDir = Join-Path $WorkDir "s10-outer-mt"
        New-Item -ItemType Directory -Path $sDir -Force | Out-Null
        Write-IsolatedNuGetConfig $sDir
        $pDir = Join-Path $sDir "SubProj"; New-Item -ItemType Directory -Path $pDir -Force | Out-Null
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit><RestorePackagesPath>$packagesDir</RestorePackagesPath></PropertyGroup>
  <ItemGroup><PackageReference Include="Pkg.Alpha" Version="1.0.0" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $pDir "SubProj.csproj") -Encoding UTF8

        $outerBl1 = Join-Path $sDir "outer-restore.binlog"
        $outerBl2 = Join-Path $sDir "outer-dgspec.binlog"
        # Run from parent $sDir with relative RestoreGraphOutputPath=rel.dgspec.json under outer /mt!
        $r1 = Invoke-CoreMsBuild $sDir @("SubProj\SubProj.csproj", "/t:Restore", "/p:RestoreUseStaticGraphEvaluation=true", "/mt", "/bl:$outerBl1", "/nologo", "/v:normal")
        $r2 = Invoke-CoreMsBuild $sDir @("SubProj\SubProj.csproj", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=rel.dgspec.json", "/mt", "/bl:$outerBl2", "/nologo", "/v:normal")
        $i1 = Invoke-Helper @("inspect-binlog", $outerBl1)
        $i2 = Invoke-Helper @("inspect-binlog", $outerBl2)
        $relFileInProjDir = Test-Path (Join-Path $pDir "rel.dgspec.json")
        $noTh1 = $i1.Output -match "TASKHOST_MSGS=0"
        $noTh2 = $i2.Output -match "TASKHOST_MSGS=0"
        $ok = ($r1.ExitCode -eq 0) -and ($r2.ExitCode -eq 0) -and $noTh1 -and $noTh2 -and $relFileInProjDir
        Record-Result "Synthetic" "10-OuterMT-InProcTasks-RelativePathsUnderStrictCWD" $ok "restoreTH0=$noTh1 dgspecTH0=$noTh2 relInProjDir=$relFileInProjDir"
    } catch { Record-Result "Synthetic" "10-OuterMT-InProcTasks-RelativePathsUnderStrictCWD" $false $_.Exception.Message }

    # --- Scenario 11: Missing DOTNET_HOST_PATH env var with non-MT task requiring TaskHost ---
    try {
        $sDir = Join-Path $WorkDir "s04-nonmt-stdout"
        $dg = Join-Path $sDir "no-host-path.dgspec.json"
        $r = Invoke-CoreMsBuild $sDir @("Noisy.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dg", "/nologo", "/v:normal") @{} -UnsetDotnetHostPath
        $ok = ($r.ExitCode -eq 0) -and (Test-Path $dg)
        Record-Result "Synthetic" "11-MissingDotnetHostPath-TaskHostBootstrap" $ok "exit=$($r.ExitCode)"
    } catch { Record-Result "Synthetic" "11-MissingDotnetHostPath-TaskHostBootstrap" $false $_.Exception.Message }

    # --- Scenario 12: Relative BinaryLoggerParameters under MT strict mode ---
    try {
        $sDir = Join-Path $WorkDir "s02-cpm"
        $r = Invoke-CoreMsBuild $sDir @("Cpm.slnx", "/t:Restore", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableBinaryLogger=true", "/p:RestoreStaticGraphBinaryLoggerParameters=LogFile=rel-mt.binlog", "/p:RestorePackagesPath=$packagesDir", "/nologo", "/v:normal")
        $blPath = Join-Path $sDir "rel-mt.binlog"
        $exists = Test-Path $blPath
        $info = if ($exists) { (Invoke-Helper @("inspect-binlog", $blPath)).Output } else { "MISSING" }
        $ok = ($r.ExitCode -eq 0) -and $exists -and ($info -match "ERRORS=0")
        Record-Result "Synthetic" "12-RelativeBinaryLoggerUnderStrictMode" $ok "exists=$exists $info"
    } catch { Record-Result "Synthetic" "12-RelativeBinaryLoggerUnderStrictMode" $false $_.Exception.Message }

    # --- Scenario 13: Legacy PackageSpecFactory (NUGET_USE_NEW_PACKAGESPEC_FACTORY=false) parity ---
    try {
        $sDir = Join-Path $WorkDir "s01-multitfm"
        $dgSt = Join-Path $sDir "legacy-st.dgspec.json"
        $dgMt = Join-Path $sDir "legacy-mt.dgspec.json"
        $rSt = Invoke-CoreMsBuild $sDir @("MultiTfm.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal") @{ NUGET_USE_NEW_PACKAGESPEC_FACTORY = "false" }
        $rMt = Invoke-CoreMsBuild $sDir @("MultiTfm.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal") @{ NUGET_USE_NEW_PACKAGESPEC_FACTORY = "false" }
        $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
        $ok = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0)
        Record-Result "Synthetic" "13-LegacyPackageSpecFactory-Parity" $ok "dgspec=$($cmp.Output)"
    } catch { Record-Result "Synthetic" "13-LegacyPackageSpecFactory-Parity" $false $_.Exception.Message }

    # --- Scenario 14: Desktop MSBuild.exe (net472 AppDomain) 18.14 MT + 18.8 Version-Gate Fallback ---
    if (-not [string]::IsNullOrWhiteSpace($Net472StageDir) -and (Test-Path $Net472StageDir)) {
        try {
            $sDir = Join-Path $WorkDir "s14-desktop"
            New-Item -ItemType Directory -Path $sDir -Force | Out-Null
            Write-IsolatedNuGetConfig $sDir
            $slnProjs = @()
            for ($i = 1; $i -le 6; $i++) {
                $pDir = Join-Path $sDir "Fx$i"; New-Item -ItemType Directory -Path $pDir -Force | Out-Null
                @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net472</TargetFramework><NuGetAudit>false</NuGetAudit><RestorePackagesPath>$packagesDir</RestorePackagesPath></PropertyGroup>
  <ItemGroup><PackageReference Include="Pkg.NetFx" Version="1.0.0" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $pDir "Fx$i.csproj") -Encoding UTF8
                $slnProjs += "  <Project Path=`"Fx$i\Fx$i.csproj`" />"
            }
            "<Solution>`n$($slnProjs -join "`n")`n</Solution>" | Set-Content (Join-Path $sDir "Desktop.slnx") -Encoding UTF8

            $notes = @()
            $allOk = $true
            if (Test-Path $DesktopMsBuild1814) {
                $dgSt = Join-Path $sDir "d1814-st.dgspec.json"
                $dgMt = Join-Path $sDir "d1814-mt.dgspec.json"
                $blMt = Join-Path $sDir "d1814-mt.binlog"
                $rSt = Invoke-DesktopMsBuild $DesktopMsBuild1814 $sDir @("Desktop.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/nologo", "/v:normal")
                $rMt = Invoke-DesktopMsBuild $DesktopMsBuild1814 $sDir @("Desktop.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/nologo", "/v:normal") @{ RESTORE_TASK_BINLOG_PARAMETERS = "LogFile=$blMt" }
                $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
                $info = Invoke-Helper @("inspect-binlog", $blMt)
                $isMt = ($info.Output -match "NODE_IDS=([0-9,]+)") -and ($Matches[1] -ne "1") -and ($Matches[1] -ne "")
                $ok14 = ($rSt.ExitCode -eq 0) -and ($rMt.ExitCode -eq 0) -and ($cmp.ExitCode -eq 0) -and $isMt
                if (-not $ok14) { $allOk = $false }
                $notes += "MSBuild18.14:ok=$ok14(mt=$isMt,dgspec=$($cmp.Output))"
            }
            if (Test-Path $DesktopMsBuild188) {
                $dg8 = Join-Path $sDir "d188.dgspec.json"
                $bl8 = Join-Path $sDir "d188.binlog"
                $r8 = Invoke-DesktopMsBuild $DesktopMsBuild188 $sDir @("Desktop.slnx", "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dg8", "/nologo", "/v:normal") @{ RESTORE_TASK_BINLOG_PARAMETERS = "LogFile=$bl8" }
                $info8 = Invoke-Helper @("inspect-binlog", $bl8)
                $isSt = ($info8.Output -match "NODE_IDS=1;")
                $ok8 = ($r8.ExitCode -eq 0) -and $isSt
                if (-not $ok8) { $allOk = $false }
                $notes += "MSBuild18.8-Fallback:ok=$ok8(st=$isSt)"
            }
            Record-Result "Synthetic" "14-DesktopMSBuild-18.14-MT-And-18.8-Fallback" $allOk ($notes -join "; ")
        } catch { Record-Result "Synthetic" "14-DesktopMSBuild-18.14-MT-And-18.8-Fallback" $false $_.Exception.Message }
    }
}

# ==============================================================================
# PART B: REAL-WORLD SOLUTIONS PARITY MATRIX
# ==============================================================================
if (-not $SkipRealSolutions -and $RealSolutions.Count -gt 0) {
    Write-Host "`n=== PART B: Real-World Solutions Parity Matrix ($($RealSolutions.Count) solutions) ===" -ForegroundColor Cyan

    $realOutDir = Join-Path $WorkDir "_real"
    New-Item -ItemType Directory -Path $realOutDir -Force | Out-Null

    foreach ($slnFullPath in $RealSolutions) {
        if (-not (Test-Path $slnFullPath)) {
            Record-Result "RealSolution" $slnFullPath $false "Solution path not found"
            continue
        }
        $slnDir = Split-Path -Parent $slnFullPath
        $slnFile = Split-Path -Leaf $slnFullPath
        $repoName = Split-Path -Leaf $slnDir
        $label = "$repoName/$slnFile"
        $safe = ($label -replace '[^a-zA-Z0-9._-]', '_')

        $dgSt = Join-Path $realOutDir "$safe-st.dgspec.json"
        $dgMt = Join-Path $realOutDir "$safe-mt.dgspec.json"

        $thBefore = Get-TaskHostCount
        $rSt = Invoke-CoreMsBuild $slnDir @($slnFile, "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreStaticGraphEnableMultiThreaded=false", "/p:RestoreGraphOutputPath=$dgSt", "/mt", "/nologo", "/v:normal")
        $rMt = Invoke-CoreMsBuild $slnDir @($slnFile, "/t:GenerateRestoreGraphFile", "/p:RestoreUseStaticGraphEvaluation=true", "/p:RestoreGraphOutputPath=$dgMt", "/mt", "/nologo", "/v:normal")
        $thAfter = Get-TaskHostCount

        $exitParity = ($rSt.ExitCode -eq $rMt.ExitCode)
        $jsonStatus = "N/A"
        $jsonOk = $true
        if ($rSt.ExitCode -eq 0 -and $rMt.ExitCode -eq 0) {
            $cmp = Invoke-Helper @("compare-json", $dgSt, $dgMt)
            $jsonStatus = $cmp.Output
            $jsonOk = ($cmp.ExitCode -eq 0)
        } elseif ($rSt.ExitCode -ne 0 -and $rMt.ExitCode -ne 0) {
            $jsonStatus = "BOTH_FAILED(ec=$($rSt.ExitCode))"
        } else {
            $jsonStatus = "EXIT_MISMATCH(ST=$($rSt.ExitCode),MT=$($rMt.ExitCode))"
            $jsonOk = $false
        }

        $leaksOk = ($thAfter -le $thBefore)
        $ok = $exitParity -and $jsonOk -and $leaksOk
        $speedup = if ($rSt.EvalMs -gt 0 -and $rMt.EvalMs -gt 0) { [math]::Round($rSt.EvalMs / [double]$rMt.EvalMs, 2) } else { 0 }
        Record-Result "RealSolution" $label $ok "ec=$($rSt.ExitCode)/$($rMt.ExitCode) projs=$($rMt.ProjCount) eval=$($rSt.EvalMs)ms->$($rMt.EvalMs)ms (${speedup}x) dgspec=$jsonStatus th=$thBefore->$thAfter"
    }
}

$passed = @($results | Where-Object { $_.Passed }).Count
$failed = @($results | Where-Object { -not $_.Passed }).Count
Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host ("SUMMARY: {0} PASSED, {1} FAILED (Total {2})" -f $passed, $failed, $results.Count) -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
Write-Host "=============================================" -ForegroundColor Cyan

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $results | ConvertTo-Json -Depth 4 | Set-Content $ReportPath -Encoding UTF8
}

if ($failed -gt 0) { exit 1 }
exit 0
