# RestoreStateAnalyzer

A Roslyn-based, whole-program **cross-assembly** reachability analyzer that finds all **static state**
and **lingering process state** (child processes, env-var edits, cwd/console/culture/registry/AppDomain
mutations, etc.) reachable from the NuGet MSBuild restore task entry points used by a regular
`dotnet restore`.

## Why a binlog-driven tool instead of a classic `DiagnosticAnalyzer`

A normal `DiagnosticAnalyzer` only sees one compilation and only *metadata* (not source) for referenced
assemblies, so it cannot follow a call from `RestoreTask` (in `NuGet.Build.Tasks`) into the body of, say,
`NuGet.Protocol` or `NuGet.Packaging`. To be **accurate across assemblies** this tool instead:

1. Builds `NuGet.Build.Tasks` for **net10.0** (the CoreCLR `dotnet restore` runtime) producing an MSBuild
   **binary log** (`/bl`).
2. Reads the exact **csc command lines** from the binlog
   (`Microsoft.Build.Logging.StructuredLogger.CompilerInvocationsReader`). This gives the precise source
   files, references, `#define`s (`IS_CORECLR`, `NET8_0_OR_GREATER`, …), `nullable` and `langversion` for
   every project — identical to the shipping build.
3. Reconstructs **one `CSharpCompilation` per NuGet source project**, wiring references between NuGet
   projects as Roslyn **`CompilationReference`s** (so symbols resolve into the *source* of every assembly)
   and running the same **source generators** (e.g. `System.Text.Json`) so generated partials resolve.
   Result: 13 assemblies, **0 binding errors**.
4. Seeds the restore **task entry points** and performs an interprocedural worklist walk over Roslyn
   `IOperation` trees, following invocations / object creations / property+event accesses / static
   initializers, expanding virtual & interface dispatch to **all** source overrides/implementations
   **transitively** (interface → abstract base → concrete override).
5. Records static-state and process-state sinks, each with the set of restore tasks that reach it and a
   representative call path, and emits `report.md`.

## Closure analyzed (regular `dotnet restore`, CoreCLR)

`NuGet.Build.Tasks` (net10.0) + `NuGet.Commands`, `NuGet.Protocol`, `NuGet.Packaging`, `NuGet.Configuration`,
`NuGet.ProjectModel`, `NuGet.DependencyResolver.Core`, `NuGet.LibraryModel`, `NuGet.Credentials`,
`NuGet.Common`, `NuGet.Frameworks`, `NuGet.Versioning`, `NuGet.Build.Tasks.Pack` (net8.0).
`NuGet.PackageManagement` and `NuGet.Resolver` are **not** referenced on this path (they are net472-only).

## Run

```powershell
# 1. produce the binlog (once)
dotnet restore src\NuGet.Core\NuGet.Build.Tasks\NuGet.Build.Tasks.csproj
dotnet build   src\NuGet.Core\NuGet.Build.Tasks\NuGet.Build.Tasks.csproj -f net10.0 --no-restore -bl:logs\2.binlog

# 2. analyze
dotnet run -c Debug -- <path-to.binlog> <report.md> [probe ...]
```

Optional trailing args are diagnostics:
- `Substring` — list reachable methods whose display contains the substring (with REACHED/queued state).
- `MAP:Substring` — dump override/implementation map entries whose key contains the substring.

## Files

- `CompilationSet.cs` — binlog → per-project `CSharpCompilation`s (+ generators, CompilationReferences).
- `Sinks.cs` — classification of process-global / lingering-state sinks.
- `Analyzer.cs` — interprocedural reachability engine (override map, transitive dispatch, static-init).
- `Reporter.cs` — per-task reachability + path reconstruction + markdown report.
- `Program.cs` — entry-point list and orchestration.
