# Restore process-state resetability — roadmap & evidence

- Status: **Report / roadmap** (ties together the stacked PRs below)
- Driving issue: dotnet/msbuild#13702 (Enlighten `RestoreTask`)
- Goal: make a `dotnet restore` run leave the **same observable process state as if the build process had
  exited**, so restore is safe in a **reused host** (MSBuild Server / multithreaded MSBuild) — without affecting
  Visual Studio (which does not run `RestoreTask` for its own restore).

This document is the index for the work. It summarizes *why*, the *evidence*, the *exact sequence* of changes
(each a stacked PR), and what *remains*.

---

## 1. Why

`RestoreTask` today assumes a process that **dies after each build**: plugin child processes + keep-alive
timers, the credential service, HTTP handlers/sockets, the proxy cache, environment/feature-flag caches, and
static event handlers are all reclaimed by process exit. `BuildTasksUtility.RestoreAsync` says so directly:
*"the tear downs of the plugins and similar rely on idleness and process exit."*

In a **reused** process that state leaks build-to-build: orphan plugin processes accumulate; credential / proxy /
cert state goes stale; environment-derived feature flags are frozen at first read; static event handlers pile up.

## 2. Evidence (custom cross-assembly analyzers)

The reachable state was enumerated with purpose-built Roslyn analyzers that reconstruct one `CSharpCompilation`
per NuGet project from a real restore **binlog** (exact `csc` args), wire inter-project references as
`CompilationReference`s, and walk an **interprocedural** worklist over `IOperation` with transitive
virtual/interface dispatch — i.e. accurate **across assembly boundaries**.

- **`RestoreStateAnalyzer`** (reachability from `RestoreTask` + every task on the `dotnet restore` path):
  **29 process sinks** — 17 child-process (incl. plugins), 3 timers, 2 console-redirection (static-graph only),
  3 env reads, 4 static event subscriptions — and **1356 reachable static-state members**.
- **`EnvStaticsAnalyzer`** (static fields/props whose initializer / `??=` cache transitively reaches an
  environment read): **15 cached environment statics** at baseline.
- **MSBuild `TaskAnalyzer`** (the in-box thread-safety analyzer) was also run: it flags only
  `NuGet.Build.Tasks` and its transitive-call-chain rule **dead-ends at the assembly boundary** — confirming why
  a cross-assembly analyzer was necessary.

(Findings + the CWD analysis were posted to dotnet/msbuild#13702.)

## 3. Exact sequence to achieve resetability (the stacked PRs)

Each step is a separate, reviewable, **stacked** PR in this fork. Order matters: each builds on the previous.

| Step | PR (fork) | Branch | What it does |
| ---- | --------- | ------ | ------------ |
| 0 | spec | `dev-JanProvaznik-envvar-traits-spec` | Spec: replace cached env statics with a resettable `NuGetTraits` singleton (MSBuild `Traits`-style) in the baseline assembly. Appendix B = analyzer inventory of the 15 cached env statics with permalinks. |
| 1 | state-1 | `dev-JanProvaznik-state-1-traits` | Add `NuGetTraits` to **NuGet.Common** (lowest common assembly). Migrate the first batch of cached env flags onto it (STJ deserialization, `NuGetTestMode`, package-id validation, default-credentials override). Analyzer-validated: 15 → 12 cached env statics. |
| 2 | state-2 | `dev-JanProvaznik-state-2-reset` | **End-of-build reset.** `RestoreProcessStateCleanup : IDisposable` registered via `IBuildEngine4.RegisterTaskObject(..., Build, allowEarlyCollection: false)`; on dispose it disposes/reset the *owned* state (PluginManager → kills plugin processes + timers, ProxyCache, ownership-gated credential service, `NuGetTraits`). Off by default; ownership-tracked so it never touches VS-owned state. |
| 3 | state-3 | `dev-JanProvaznik-state-3-reset-at-start` | **Reset at start of restore.** `NuGetTraits.UpdateFromEnvironment()` at the top of `RestoreTask.Execute`, so even a process whose prior cleanup didn't run starts each build from the current environment. |
| 4 | state-4 | `dev-JanProvaznik-state-4-taskenvironment` | **Read the environment from the task, not the process.** `NuGetTraits.UpdateFromEnvironment(IEnvironmentVariableReader)` made public; new `TaskEnvironmentVariableReader` adapts `Func<string,string?>` (the seam that binds `TaskEnvironment.GetEnvironmentVariable`); start-of-restore reset reads through the task's reader. Spec: `restore-taskenvironment-reset.md`. |
| 5 | state-5 | `dev-JanProvaznik-state-5-more-traits` | **Migrate two more cached env flags.** `ConcurrencyUtilities` delete-on-close (`NUGET_ConcurrencyUtils_DeleteOnClose`) and `DependencyGraphSpec` legacy hash (`NUGET_ENABLE_LEGACY_DGSPEC_HASH_FUNCTION`) onto `NuGetTraits`. Analyzer-validated: 12 → 10. |

Gating: all reset behavior is **off by default**, opt-in via the `ResetProcessStateAfterBuild` MSBuild property
or `NUGET_RESTORE_RESET_PROCESS_STATE` env var. Classic single-shot `dotnet build` and any VS-hosted path are
unaffected. **No reflection** anywhere (repo guideline).

## 4. Curated reset inventory (what we reset vs. deliberately leave)

**Dispose** (live OS resources / background work): `PluginManager` (plugin **processes**, keep-alive **timers**,
connections); HTTP handler caches (sockets / connection pools — follow-on).

**Reset / re-init** (stale per-build/per-env config): credential service (only if restore created it),
`ProxyCache.Instance`, environment/feature-flag one-shots via `NuGetTraits`.

**Deliberately leave** (a fresh process would rebuild these *identically*, so resetting is pure perf loss):
comparer/`Instance` singletons, `ResourceManager`, framework-mapping `Lazy<>` tables, interning dictionaries,
parse caches, JSON source-gen contexts, object pools, machine-invariant helpers, idempotent cert factories;
`[ThreadStatic]` scratch buffers (per-thread, cleared-on-use) — out of scope, noted for a memory follow-up.

## 5. Remaining work

Analyzer status: cached environment statics reduced **15 → 10** across the stack; `NuGetTraits._instance` is the
single intended resettable holder. The remaining 9 are not simple boolean flags — they are singletons, path
lazies, test seams, and policy/throttle objects with distinct reset semantics:

- **Migrate the remaining cached environment statics** (drive 10 → only `NuGetTraits._instance`): `NuGetEnvironment`
  home/temp paths, `SourceRepositoryDependencyProvider._throttle` (env-sized semaphore),
  `X509ChainBuildPolicyFactory.Policy`, and the singleton holders (`PluginManager`/`PluginLogger`/`ExceptionLogger`,
  plus the `StreamExtensions`/`ZipArchiveExtensions` test seams). Re-run `EnvStaticsAnalyzer` after each batch.
- **HttpSource handler-cache disposal** via the provider factory (release sockets on reset).
- **Enlighten `RestoreTask` as `IMultiThreadableTask`** once MSBuild ships `TaskEnvironment` in the referenced
  `Microsoft.Build.Framework`: annotate `[MSBuildMultiThreadableTask]`, bind `TaskEnvironment.GetEnvironmentVariable`
  into `TaskEnvironmentVariableReader`, and migrate CWD/path/process APIs (`Path.GetFullPath`,
  `Environment.CurrentDirectory`, `ProcessStartInfo`) to `TaskEnvironment.GetAbsolutePath` /
  `GetProcessStartInfo` (the larger mechanical follow-on; see `restore-taskenvironment-reset.md`).
- **Concurrency end-state**: the current design assumes builds in one process are **sequential** (Server's
  model). Overlapping concurrent builds would need the build-scoped/plumbed model — noted as future-robust.
