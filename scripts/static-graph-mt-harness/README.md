# NuGet Static-Graph Multithreaded Restore — Parity & Benchmark Harness

Reproducible integration test and benchmark harness validating [dotnet/msbuild#13701](https://github.com/dotnet/msbuild/issues/13701).

## What is included

- `Run-StaticGraphMtParityHarness.ps1` — runs **14 self-contained synthetic edge-case scenarios** + any set of **real-world solutions**, asserting:
  - Exact canonicalized `dgspec.json` parity (and `project.assets.json` parity for offline end-to-end restore)
  - Exit-code and diagnostic/warning parity
  - Zero lingering MSBuild TaskHost (`/nodemode:2`) processes after exit
  - Multi-node concurrency via binary-log replay (`NODE_IDS`)
  - Opt-out switches, precedence, option-injection defense, single-project gating, outer `/mt` in-proc execution, missing `DOTNET_HOST_PATH`, relative binlog under MSBuild 18.12 strict CWD mode, legacy `PackageSpecFactory`, and Desktop `MSBuild.exe` (18.14 MT + 18.8 version-gated fallback)
- `Run-StaticGraphMtBenchmarks.ps1` — interleaved paired A/B (`AB`/`BA`) benchmark reporting `min`/`median` for both `LoadProjects` (`Evaluated N project(s) in Xms`) and end-to-end wall time.

## How to run

```powershell
# 1. Build Release net10.0 + net472 NuGet.Build.Tasks.Console
dotnet build src\NuGet.Core\NuGet.Build.Tasks.Console\NuGet.Build.Tasks.Console.csproj -c Release

# 2. Stage built NuGet.*.dll + NuGet.targets + NuGet.RestoreEx.targets into a local SDK copy
#    and point -StagedSdkRoot / -Net472StageDir at the staged folders.

# 3. Run parity harness (synthetic + real solutions)
.\scripts\static-graph-mt-harness\Run-StaticGraphMtParityHarness.ps1 `
    -StagedSdkRoot <path-to-staged-sdk> `
    -Net472StageDir <path-to-net472-bin> `
    -RealSolutions @("C:\repos\OrchardCore\OrchardCore.slnx", "C:\repos\roslyn\Roslyn.slnx")

# 4. Run 8-round interleaved paired A/B benchmarks
.\scripts\static-graph-mt-harness\Run-StaticGraphMtBenchmarks.ps1 `
    -StagedSdkRoot <path-to-staged-sdk> `
    -Rounds 8 `
    -Solutions @("C:\repos\OrchardCore\OrchardCore.slnx", "C:\repos\roslyn\Roslyn.slnx")
```

## Recorded Results (16-core Windows, .NET 11.0.100 SDK / MSBuild 18.12.2, VS 18.14 / 18.8)

### Parity Matrix — 33/33 PASSED

- **4/4 real `dotnet restore /p:RestoreUseStaticGraphEvaluation=true --force -mt` full restore parity (`637 / 637` `project.assets.json` files byte-for-byte SHA256 identical, 0 TaskHosts):**
  - `Roslyn.slnx` (`dotnet restore --force -mt`): **276 / 276** `project.assets.json` identical
  - `sdk.slnx` (`/t:Restore /p:RestoreForce=true -mt`, staged MSBuild because `sdk/global.json` pins `.dotnet`): **253 / 253** `project.assets.json` identical
  - `TestFx.slnx` (`dotnet restore --force -mt`): **67 / 67** `project.assets.json` identical
  - `MSBuild.slnx` (`dotnet restore --force -mt`): **41 / 41** `project.assets.json` identical
- **14/14 synthetic scenarios:** multi-TFM 64-build `dgspec` + `project.assets.json` parity across 16 nodes; CPM + `GlobalPackageReference` + transitive pinning; concurrent duplicate NuGet items (`60=60` warnings); non-MT inline `RoslynCodeTaskFactory` writing partial `Console.Write` noise (`16=16` warnings, exact CWD per project, 0 leaked TaskHosts); non-MT compiled task calling `IBuildEngine.BuildProjectFile`; diamond nested `<MSBuild>` graph; SDK unhappy-path `NETSdkWarning` (`8=8` warnings); concurrent target failure + cleanup; 7-case opt-out / single-project gate / precedence / option-injection matrix; outer `/mt` in-proc `RestoreTaskEx` & `GenerateRestoreGraphFileTask` (0 outer TaskHosts) with relative paths under strict CWD; missing `DOTNET_HOST_PATH` TaskHost bootstrap; relative binlog under strict mode; legacy `PackageSpecFactory`; Desktop `MSBuild.exe` 18.14 MT (`dgspec=MATCH`) + 18.8 version-gated ST fallback.
- **15/15 real-world solutions (`GenerateRestoreGraphFile`, offline / non-mutating `dgspec.json` diff across all repos regardless of online feed availability or repo-local `global.json`):** `OrchardCore.slnx`, `Roslyn.slnx`, `sdk.slnx`, `MSBuild.slnx`, `NuGet-UnitTests.slnf`, `TestFx.slnx`, `Arcade.slnx`, `ProjectSystem.slnx`, `EFCore.slnx`, `FSharp.slnx`, `TestPlatform.slnx`, `Winforms.sln`, `Microsoft.Dotnet.Wpf.sln` [exit-code parity], `System.CommandLine.slnx`, `sourcelink.slnx` — **100% canonicalized `dgspec.json` parity and 0 lingering TaskHost processes**.

### Paired A/B Benchmarks — Real `dotnet restore /p:RestoreUseStaticGraphEvaluation=true -mt` (6 interleaved rounds, warm no-op)

| Solution | Builds | LoadProjects ST min/med (ms) | LoadProjects MT min/med (ms) | LoadProjects Speedup | Wall ST min/med (ms) | Wall MT min/med (ms) | Wall Delta |
|---|---:|---:|---:|---:|---:|---:|---:|
| `sdk.slnx` (`/t:Restore -mt`) | 337 | 5,079 / 5,675 | **2,495 / 3,352** | **2.04x (-50.9%)** | 6,559 / 7,172 | **3,859 / 4,805** | **-41.2%** |
| `TestFx.slnx` (`dotnet restore -mt`) | 211 | 3,073 / 3,199 | **1,579 / 1,600** | **1.95x (-48.6%)** | 4,729 / 5,019 | **3,297 / 3,328** | **-30.3%** |
| `Roslyn.slnx` (`dotnet restore -mt`) | 504 | 7,666 / 8,275 | **5,135 / 5,871** | **1.49x (-33.0%)** | 10,189 / 11,060 | **7,385 / 8,481** | **-27.5%** |
| `MSBuild.slnx` (`dotnet restore -mt`) | 104 | 1,803 / 2,040 | **1,160 / 1,396** | **1.55x (-35.7%)** | 3,524 / 3,692 | **2,847 / 3,083** | **-19.2%** |

### Paired A/B Benchmarks — `GenerateRestoreGraphFile` (8 interleaved rounds, isolates static-graph evaluation + `_CollectRestoreInputs`)

| Solution | Builds | LoadProjects ST min/med (ms) | LoadProjects MT min/med (ms) | LoadProjects Speedup | Wall ST min/med (ms) | Wall MT min/med (ms) | Wall Delta |
|---|---:|---:|---:|---:|---:|---:|---:|
| `OrchardCore.slnx` | 454 | 5,000 / 5,410 | **2,228 / 2,818** | **2.24x (-55.4%)** | 6,248 / 6,711 | **3,447 / 4,008** | **-44.8%** |
| `Roslyn.slnx` | 504 | 5,983 / 6,287 | **3,339 / 3,466** | **1.79x (-44.2%)** | 7,263 / 7,663 | **4,602 / 4,702** | **-36.6%** |
| `sdk.slnx` | 333 | 4,425 / 4,514 | **1,996 / 2,144** | **2.22x (-54.9%)** | 5,577 / 5,683 | **3,082 / 3,255** | **-44.7%** |
| `TestPlatform.slnx` (vstest) | 236 | 2,751 / 3,068 | **1,644 / 1,789** | **1.67x (-40.2%)** | 3,675 / 4,061 | **2,556 / 2,768** | **-30.4%** |
| `TestFx.slnx` | 211 | 2,728 / 2,786 | **1,301 / 1,427** | **2.10x (-52.3%)** | 3,677 / 3,730 | **2,252 / 2,364** | **-38.8%** |
