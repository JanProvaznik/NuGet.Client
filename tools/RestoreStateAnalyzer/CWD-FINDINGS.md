# Preliminary: do the restore tasks depend on the current working directory (CWD)?

Follow-up to the enlightenment discussion. CWD dependence is the central multithreaded-MSBuild hazard
(`Environment.CurrentDirectory`, `Directory.GetCurrentDirectory`, `Path.GetFullPath`, and relative paths to
File/Directory APIs all resolve against the **process-global** working directory shared by all threads).

I attacked this two ways and cross-checked them:
1. **Ran MSBuild's own `TaskAnalyzer`** (built from `dotnet/msbuild/src/TaskAnalyzer`) against a real
   `NuGet.Build.Tasks` build (net10.0).
2. **Cross-assembly interprocedural pass** (the analyzer from the earlier comment, extended with the same
   banned-API set: `Path.GetFullPath(string)`, `Directory.GetCurrentDirectory`, `Environment.CurrentDirectory`,
   `Directory.SetCurrentDirectory`, `Environment.GetFolderPath`, `Path.GetTempPath/GetTempFileName`), seeded
   from every restore task and traced into the source of all referenced NuGet assemblies.

**TL;DR:** No restore task reads CWD *directly* on the regular path, but `RestoreTask` and
`GetRestoreSettingsTask` reach **~38 CWD / process-global path+env touchpoints transitively**, almost all of
them in `NuGet.Common`/`NuGet.Configuration`/`NuGet.Commands`/`NuGet.Packaging`/`NuGet.ProjectModel`/`NuGet.Protocol`.
The MSBuild `TaskAnalyzer` sees **none** of those — its transitive rule stops at the `NuGet.Build.Tasks` assembly
boundary. This is the cross-assembly blind spot.

## 1. What MSBuild's TaskAnalyzer reports (real build, scope=all, net10.0)

It only flags code in the compilation being built (`NuGet.Build.Tasks`):

| Rule | Where | API |
|------|-------|-----|
| MSBuildTask0001 (critical) | StaticGraphRestoreTaskBase.cs:155/158/172 | `Console.InputEncoding` |
| MSBuildTask0001 | StaticGraphRestoreTaskBase.cs:203 | `Process.Kill()` |
| MSBuildTask0002 (TaskEnvironment) | StaticGraphRestoreTaskBase.cs:134 | **`Environment.CurrentDirectory`** |
| MSBuildTask0002 | StaticGraphRestoreTaskBase.cs:122 | `ProcessStartInfo()` |
| MSBuildTask0002 | StaticGraphRestoreTaskBase.cs:353/357 | `Path.GetFullPath(string)` |
| MSBuildTask0002 | GetRestoreProjectReferencesTask.cs:63 | `Path.GetFullPath(string)` |
| MSBuildTask0002 | GetRestoreSolutionProjectsTask.cs:51 | `Path.GetFullPath(string)` |
| MSBuildTask0003 (file path) | StaticGraphRestoreTaskBase.cs:38, WriteRestoreGraphTask.cs:73 | `new FileInfo(...)` |
| MSBuildTask0005 (transitive) | GetRestoreProjectStyleTask.Execute → BuildTasksUtility.GetProjectRestoreStyle → … → `File.Exists` | within NuGet.Build.Tasks |
| MSBuildTask0005 | GetRestoreSettingsTask.Execute → RestoreSettingsUtils.ReadSettings → `Path.GetFullPath` | within NuGet.Build.Tasks |
| MSBuildTask0005 | RestoreTask.Execute → ExecuteAsync → GetFilesToEmbedInBinlog → BuildTasksUtility.GetPackagesConfigFilePath → `File.Exists` | within NuGet.Build.Tasks |

Note the only `Environment.CurrentDirectory` and most `ProcessStartInfo`/`Process` hits are on the **static-graph**
path (`StaticGraphRestoreTaskBase`, used by `RestoreTaskEx`/`GenerateRestoreGraphFileTask`) — which is out-of-proc
and arguably a separate concern. The regular `RestoreTask` only shows a transitive `File.Exists` via
`BuildTasksUtility` (binlog-embedding no-op path).

### The cross-assembly limitation (confirmed from the analyzer source)

`TransitiveCallChainAnalyzer` (MSBuildTask0005) builds its call graph from `RegisterOperationAction`, i.e. **only
from operations in the current compilation's source**. When a call reaches a method in a referenced assembly
(metadata), that node has no recorded out-edges, so the BFS dead-ends. Every 0005 chain above terminates inside
`NuGet.Build.Tasks` (`BuildTasksUtility`, `RestoreSettingsUtils`). It cannot follow
`RestoreTask → BuildTasksUtility.RestoreAsync → NuGet.Commands.RestoreRunner → …`. It is also opt-in/scoped and
only analyzes `ITask` implementations (plus `[MSBuildMultiThreadableTaskAnalyzed]` helpers) — NuGet's library code
isn't analyzed at all.

## 2. What the cross-assembly pass finds (the part 0005 misses)

38 CWD / process-global path+env touchpoints reachable from the restore tasks. The `NuGet.Build.Tasks` ones
overlap with the analyzer above; **the rest are invisible to it.** Reachable-from-`[regular restore]` unless noted.

**Explicit CWD / process-env reads (highest signal):**
- `Environment.CurrentDirectory` (get) — `NuGet.Protocol` `PluginLogger.cs:43` (reachable from `RestoreTask`)
- `Directory.GetCurrentDirectory` — `NuGet.Common` `PathUtility.CheckIfFileSystemIsCaseInsensitive` (`PathUtility.cs:457`) — reached from `RestoreTask`, `GetRestoreSettingsTask`, `WarnForInvalidProjectsTask`, `WriteRestoreGraphTask`
- `Environment.GetFolderPath` — `NuGet.Common`: `NuGetEnvironment.GetHome` (`:295`), `Migration1` (`:61/106/117`), `MigrationRunner.GetMigrationsDirectory` (`:92/98`)
- `Path.GetTempPath` — `NuGet.Common` `NuGetEnvironment.GetNuGetTempDirectory` (`:47`)
- `Path.GetTempFileName` — `NuGet.Protocol` `FindPackagesByIdNupkgDownloader.CopyNupkgToStreamAsync` (`:120`)
- `Environment.CurrentDirectory` (get) — `NuGet.Build.Tasks` `StaticGraphRestoreTaskBase:134` _(static-graph only)_

**`Path.GetFullPath(string)` (resolves relative → CWD) — 27 sites:**
- `NuGet.Common`: `UriUtility.GetAbsolutePath` (`:127/134`), `PathUtility.GetFirstParentDirectoryThatExists` (`:485`), `PathValidator` (`:49/74/99`), `ConcurrencyUtilities.FilePathToLockName` (`:261`), `FileUtility.GetTempFilePath` (`:26`)
- `NuGet.Configuration`: `SettingsFile..ctor` (`:108`), `SettingsUtility` `GetGlobalPackagesFolder`/`GetFallbackPackageFolders`/`GetHttpCacheFolder`/`GetPluginsCacheFolder` (`:269/316/359/381`)
- `NuGet.Commands`: `RestoreRunner.GetRequests` (`:179/207`), `RestoreCommand.GetAssetsFilePath` (`:1566`), `NoOpRestoreUtilities.GetCacheFilePath` (`:94`)
- `NuGet.ProjectModel`: `JsonPackageSpecReader.GetPackageSpec` (`:117/164`)
- `NuGet.Packaging`: `PackageReaderBase.ValidatePackageEntry`/`NormalizeDirectoryPath` (`:556/575`)
- `NuGet.Build.Tasks` (also seen by MSBuild analyzer): `GetRestoreProjectReferencesTask:63`, `GetRestoreSolutionProjectsTask:51`, `RestoreSettingsUtils:37/57`, `StaticGraphRestoreTaskBase:353/357`

## 3. Per-task verdict

| Task | Direct CWD | Transitive (cross-assembly) CWD/path/env |
|------|-----------|------------------------------------------|
| `RestoreTask` | none | **heavy** — UriUtility/SettingsUtility/SettingsFile, RestoreCommand/RestoreRunner/NoOpRestoreUtilities, JsonPackageSpecReader, PackageReaderBase, PathUtility (`GetCurrentDirectory`), NuGetEnvironment (`GetFolderPath`/`GetTempPath`), PluginLogger (`Environment.CurrentDirectory`), FindPackagesByIdNupkgDownloader (`GetTempFileName`), Migration1 (`GetFolderPath`) |
| `GetRestoreSettingsTask` | none (uses `MSBuildStartupDirectory`) | UriUtility/SettingsUtility/SettingsFile/RestoreSettingsUtils `Path.GetFullPath`; PathUtility `GetCurrentDirectory`; NuGetEnvironment `GetFolderPath`/`GetTempPath` |
| `WriteRestoreGraphTask` | `new FileInfo` (:73) | NuGetEnvironment `GetHome`, PathUtility `GetCurrentDirectory`/`GetFullPath`, `GetTempPath` |
| `WarnForInvalidProjectsTask` | none | NuGetEnvironment/PathUtility (via MSBuildRestoreUtility) |
| `GetRestoreProjectStyleTask` | none | `BuildTasksUtility → File.Exists` (rooted in `MSBuildProjectDirectory`) |
| `GetRestoreProjectReferencesTask` | `Path.GetFullPath` (:63) — base = `ParentProjectPath` (abs in practice) | — |
| `GetRestoreSolutionProjectsTask` | `Path.GetFullPath` (:51) — base = solution dir (abs in practice) | — |
| `GetRestoreDotnetCliToolsTask`, `GetProjectTargetFrameworksTask`, `GetRestorePackageReferencesTask`, `GetCentralPackageVersionsTask`, `GetRestorePackageDownloadsTask`, `GetRestoreFrameworkReferencesTask`, `GetRestoreNuGetAuditSuppressionsTask`, `GetRestorePrunePackageReferencesTask`, `CheckForDuplicateNuGetItemsTask`, `NuGetMessageTask`, `GetGlobalPropertyValueTask`, `GetReferenceNearestTargetFrameworkTask` | none | none (pure item/property shaping) |

## 4. Important nuances (preliminary)

- **API presence ≠ actual CWD bug.** Most `Path.GetFullPath`/file-API calls in restore are fed an absolute base
  — restore deliberately threads `MSBuildStartupDirectory` (passed from the targets, not read from CWD) and the
  project directory through `BuildTasksUtility.GetSources/GetFallbackFolders` and `UriUtility.GetAbsolutePath`.
  So today they happen to be CWD-independent in practice. But enlightenment still has to migrate them
  (`TaskEnvironment.GetAbsolutePath`) because the *API* is CWD-sensitive and the safety can't be guaranteed
  statically.
- **The genuinely process-global reads** that don't take an explicit base: `Directory.GetCurrentDirectory`
  (`PathUtility.CheckIfFileSystemIsCaseInsensitive`), `Environment.CurrentDirectory` (`PluginLogger`),
  `Environment.GetFolderPath`/`Path.GetTempPath`/`GetTempFileName` — these read process/user environment and are
  the ones most worth auditing first. Several are also cached in `Lazy<>` statics (see the earlier process-state
  comment), so the value is captured once per process.
- **Scope:** results are the regular `dotnet restore` CoreCLR closure (`NuGet.Build.Tasks` net10.0 + libraries
  net8.0). The static-graph path (`RestoreTaskEx`) additionally has `Environment.CurrentDirectory` +
  `ProcessStartInfo` directly, but it runs out-of-proc and self-terminates.
- This is a **first pass**: `Path.GetFullPath` 2-arg and the broad MSBuildTask0003 file-API pattern (every
  `File.*`/`Directory.*`/`FileInfo` with a possibly-relative string) are not fully enumerated here; the count of
  *potential* file-path sites cross-assembly is much larger.

Tool + full per-finding call paths:
https://github.com/JanProvaznik/NuGet.Client/tree/dev-JanProvaznik-restoreStateAnalysis/tools/RestoreStateAnalyzer

Happy to produce the exhaustive MSBuildTask0003 cross-assembly list, or per-finding call chains for any of the above.
