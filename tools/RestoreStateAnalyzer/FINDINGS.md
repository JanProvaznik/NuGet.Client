# NuGet restore — static & lingering process state reachable from the restore MSBuild tasks

Investigation into what static / process-global state a regular `dotnet restore` touches, motivated
by the question: **if the process hosting `RestoreTask` becomes long-lived** (MSBuild Server, the
multithreaded-MSBuild effort, or any reused worker that no longer dies after each build), **what state
leaks across restores and what must be invalidated or torn down?**

The numbers and call paths below were produced by the `RestoreStateAnalyzer` tool in this folder.

## Method (why it is accurate cross-assembly)

A classic `DiagnosticAnalyzer` only sees one compilation and *metadata* (not source) of references, so it
cannot follow a call from `RestoreTask` (`NuGet.Build.Tasks`) into `NuGet.Protocol`/`NuGet.Packaging`. This
tool instead:

1. Builds `NuGet.Build.Tasks` for **net10.0** (the CoreCLR `dotnet restore` runtime) with a binary log.
2. Reads the **exact csc command lines** from the binlog (`CompilerInvocationsReader`) — precise sources,
   references, `#define`s (`IS_CORECLR`, `NET8_0_OR_GREATER`), nullable, langversion.
3. Reconstructs **one `CSharpCompilation` per NuGet project**, wiring inter-project references as Roslyn
   `CompilationReference`s (symbols resolve into the *source* of every assembly) and running the same
   **source generators** (System.Text.Json). Result: **13 assemblies, 0 binding errors**.
4. Seeds the restore task entry points and walks `IOperation` trees interprocedurally, expanding
   virtual/interface dispatch to **all** source overrides/implementations **transitively**
   (interface → abstract base → concrete override).

**Closure analyzed** (regular `dotnet restore`, CoreCLR): `NuGet.Build.Tasks` (net10.0) + `NuGet.Commands`,
`NuGet.Protocol`, `NuGet.Packaging`, `NuGet.Configuration`, `NuGet.ProjectModel`,
`NuGet.DependencyResolver.Core`, `NuGet.LibraryModel`, `NuGet.Credentials`, `NuGet.Common`,
`NuGet.Frameworks`, `NuGet.Versioning`, `NuGet.Build.Tasks.Pack` (net8.0). `NuGet.PackageManagement` and
`NuGet.Resolver` are **not** on this path (net472-only). **7,267** reachable methods/static-init nodes.

**Entry points** (all resolved): regular path = `RestoreTask` + the DG-spec collection tasks
(`Get*`, `WriteRestoreGraphTask`, `CheckForDuplicateNuGetItemsTask`, `WarnForInvalidProjectsTask`,
`NuGetMessageTask`, `GetGlobalPropertyValueTask`); static-graph (opt-in) = `RestoreTaskEx`,
`GenerateRestoreGraphFileTask`.

## Headline

A regular `dotnet restore` through `RestoreTask` **does not mutate env vars, cwd, culture or the registry**
(0 such sinks) — a persistent host will not get its own ambient environment corrupted. The hazards are
narrower and concentrated in `NuGet.Protocol`'s plugin / HTTP / credential code plus a few static caches
and static events.

### Process-global ("lingering") sinks reachable — 29

| Category | Count | Reachable from regular `dotnet restore`? |
|---|---:|:---:|
| ChildProcess | 17 | YES |
| EnvVarRead | 3 | YES |
| StaticEventSubscription | 4 | YES |
| TimerCreation | 3 | YES |
| ConsoleRedirection | 2 | static-graph only |
| EnvVarWrite / CwdChange / CultureChange / RegistryWrite / AppContextMutation / AppDomainHandler | 0 | — |

### Static state reachable — 1,356 members

| Kind | Count |
|---|---:|
| MutableStaticField | 53 |
| MutableStaticProperty | 76 |
| StaticReadonlyMutableRef | 482 |
| LazyStaticField | 35 |
| ThreadStaticField | 7 |
| StaticReadonlyImmutable | 703 |

## Persistent-host hazard analysis

State that used to be reclaimed by process exit and now leaks across restores, ordered by required action.

### Tier 1 — live OS resources you must explicitly tear down

- **Plugin child processes (the main one).** `PluginManager.Instance` is a static `Lazy<IPluginManager>`
  singleton owning `PluginFactory`, which caches running plugin processes (credential + .NET auth/download
  plugins) for the process lifetime, each with keep-alive/idle **timers** (`Plugin.cs:126`,
  `OutboundRequestContext` timeout timers, `AutomaticProgressReporter.cs:36`). Reachable sinks:
  `PluginProcess` ctor / `Start` / `Kill`, `PluginFactory` `ProcessStartInfo`. Persistent host → orphaned
  plugin processes + timer threads accumulate. Dispose `PluginManager` on shutdown / idle-evict between
  sessions. `_lazy`/`_currentProcessId` also capture env (`NUGET_PLUGIN_PATHS`, pid) once and never refresh.
- **HTTP connection pools / handlers.** `HttpSourceResourceProvider` caches `HttpSource` per source
  (HttpClient + handler chain → sockets, proxy, cached creds); `HttpHandlerResourceV3.CredentialService`
  is static. Dispose on shutdown; invalidate when sources/proxy/creds change.
- **Semaphores/throttles** (process-global): `SourceRepositoryDependencyProvider._throttle`,
  `CredentialService.ProviderSemaphore` (a *named* OS `Semaphore`), `HttpSourceAuthenticationHandler._credentialPromptLock`,
  `HttpSourceResourceProvider.Throttle`. A permit leaked on an exception path now stays leaked for the
  host lifetime → progressive throughput collapse. Audit the try/finally.

### Tier 2 — cached config that goes STALE across restores (correctness)

- **`DefaultCredentialServiceUtility.DelegatingLogger` + `HttpHandlerResourceV3.CredentialService`** — static
  and mutated; they hold the *current* restore's logger/credential service. Without a per-restore reset you
  get cross-talk and a pinned logger. **Reset per restore.**
- **`ProxyCache.Instance`** (Lazy) — proxy + creds read once; stale if they change.
- **Env/feature-flag one-shots**: `NuGetTestMode.Enabled`, `PackageIdValidator.IsValidationDisabled`,
  `NuGetFeatureFlags._isSystemTextJsonDeserializationEnabledByEnvironment`, `UserAgent.UserAgentString`,
  `NuGetEnvironment._getHome/_nuGetTempDirectory`, `RuntimeEnvironmentHelper.*`,
  `ConcurrencyUtilities._basePath`. Machine-invariant ones are safe; env-derived ones won't refresh.
- **Signing/cert statics**: `X509TrustStore.CodeSigning/TimestampingX509ChainFactory`,
  `X509ChainBuildPolicyFactory.Policy`, cert-bundle factories — cached per process.

### Tier 3 — static events (handler accumulation / leak)

`ProtocolDiagnostics.HttpEvent / ResourceEvent / NupkgCopiedEvent / ServiceIndexEntryEvent` are static
events. Any `+=` that relied on process death to clean up now accumulates subscribers across restores →
duplicate delivery + memory growth. Ensure every subscribe has a matching unsubscribe (or scope per restore).

### Tier 4 — `[ThreadStatic]` scratch state (memory pinning on pooled threads)

7 fields, all resolver/runtime scratch buffers: `GraphOperations._tempDowngrades`,
`GraphOperations.Cache._dictionary/_queue/_tracker`, `RuntimeGraph.Cache._hashSet/_list`. The thread pool is
reused in a persistent host, so each pooled thread retains the last restore's graph buffers (memory pinned;
potential stale read if not cleared on entry). Verify reset-on-use; expect idle memory growth ∝ pool size.

### Tier 5 — benign (no action)

Comparer/`Instance` singletons, `Strings.resourceMan`/`resourceCulture`, `TaskResult.*`, framework-mapping
`Lazy<>` tables, interning dictionaries (`LibraryType._knownLibraryTypes`, `BuildAction._knownBuildActions`),
parse caches (`NuGetVersion`/`VersionRange` mappings — grow-only, value-keyed), JSON source-gen contexts,
object pools (`StringBuilderPool.Shared`, `ContentItemCollection.*Pool`). Immutable or grow-only.

Notably **not** a concern: the umask `chmod`/`getconf` child processes (`Migration1`, `NuGetExtractionFileIO`)
are short-lived `using` blocks that self-dispose; migrations are gated to run once; and `Console.InputEncoding`
mutation is **static-graph only** and restored in a `finally`.

## Hygiene checklist for a persistent restore host

- **Per restore**: reset the credential service + `DelegatingLogger`; unsubscribe `ProtocolDiagnostics`
  handlers; invalidate `ProxyCache` if proxy/creds can change.
- **Per session / shutdown**: dispose `PluginManager.Instance` (kills plugin processes + timers); dispose
  cached `HttpSource`/handlers.
- **Audit**: semaphore Wait/Release for leak-on-exception; `[ThreadStatic]` reset-on-entry + memory.
- **Accept as stale**: env/feature-flag/UserAgent one-shots unless explicitly reset.

## Caveats

The analyzer reports **reachability + classification**, not the lifetime/reset semantics of each member — the
Tier assignments above combine the tool's data with NuGet domain knowledge and should be confirmed against the
actual reset/dispose paths. Dispatch expansion is a sound over-approximation (it can include a target a given
run would not pick, but will not miss a statically-possible one). Pure reflection / dynamically-built delegate
dispatch is not followed (restore does not use it on these paths). Results are for the **net10.0 / net8.0**
CoreCLR closure; the .NET Framework (net472) path differs (e.g. `PluginDiscoverer.IsExecutable`'s `Process`
is `#if !NET8_0_OR_GREATER` and would add a sink there).

## Reproduce

```
dotnet restore src/NuGet.Core/NuGet.Build.Tasks/NuGet.Build.Tasks.csproj
dotnet build   src/NuGet.Core/NuGet.Build.Tasks/NuGet.Build.Tasks.csproj -f net10.0 --no-restore -bl:restore.binlog
dotnet run --project tools/RestoreStateAnalyzer -- restore.binlog report.md
```

Optional trailing args: `Substring` lists reachable methods matching the substring; `MAP:Substring` dumps the
override/implementation map. See `README.md` for design details.
