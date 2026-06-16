# Restore static-state audit — is there any real leakage?

- Status: **Audit / evidence** (companion to `restore-state-resetability-roadmap.md`)
- Question answered: *"Are you confident there is no real static leakage reachable from restore? Annotate every
  reachable static member and explain how each is mitigated or why it is not a problem."*
- Data backbone: `restore-static-state-inventory.md` — the exhaustive machine-generated enumeration of all
  **1357** reachable static members, each with a verdict + permalink.

## Headline

**Honest answer: not *zero* today — but the residual is small, fully enumerated, and non-structural.** Of 1357
reachable static members, **1346 cannot leak per-build state** (they are immutable, deterministic singletons/tables,
input-keyed caches, per-thread scratch, process-lifetime primitives, or already reset). The remaining **~11** are
environment/path/throttle caches that freeze a value on first use and are not yet re-read in a reused process. None
hold per-build *correctness* state that silently corrupts a later build's result; they would at most carry a stale
*environment* value (e.g. a temp path or concurrency limit) if the environment changed *between* builds in one
process. Every one has an obvious fix (route through the resettable `NuGetTraits` / reset hook), and all are listed
below. Event subscriptions (the double-registration vector) are audited separately in §D: restore is
**publish-only** for every process-global event, so that vector does not exist on the restore path.

## Methodology

The analyzer reconstructs one Roslyn `CSharpCompilation` per NuGet project from the restore build's exact `csc`
arguments, wires inter-project references as `CompilationReference`s, and walks an interprocedural worklist over
`IOperation` with transitive virtual/interface dispatch — so reachability and the declaring-type of every static
read/write is resolved **across assembly boundaries**. Each reachable static member is bucketed into exactly one
verdict by its kind (`const`/`readonly`/mutable/`Lazy`/`[ThreadStatic]`), its declared type's *shape*
(immutable / comparer / stateless-helper / collection / pool / live-resource), whether it is *written* on a
reachable path, and a cross-reference against the `EnvStaticsAnalyzer` env-cache set and the curated set of types
the end-of-build cleanup resets. Anything a rule could not prove safe falls into a review bucket and is
hand-adjudicated below — nothing is hidden. Static **events** are analyzed separately from static fields/properties:
the analyzer distinguishes `+=` (subscribe), `-=` (unsubscribe), and raise via `IEventAssignmentOperation.Adds`, so
only a true subscription counts toward the double-registration risk (§D).

## Verdict summary (1357 members)

| Verdict | Count | Can it leak per-build state? |
| --- | --: | --- |
| ImmutableValue | 711 | No — primitive/string/enum/immutable ref; a fresh process recreates the identical value. |
| StableSingletonOrTable | 421 | No — stateless helper / comparer / generated `ResourceManager` / constant lookup table; recreated identically. |
| InputKeyedCache | 173 | No correctness leak — memoization keyed purely on inputs; deterministic. Only unbounded **memory** in a long-lived host. |
| ThreadScratch | 7 | No — `[ThreadStatic]` scratch buffers, cleared on each use; per pooled thread. |
| ResetByCleanup | 8 | Mitigated — torn down by `RestoreProcessStateCleanup` at end of build. |
| ResetByTraits | 2 | Mitigated — `NuGetTraits` instance, re-read at start **and** end of restore. |
| EnvDerivedNotReset | 8 | **Gap** — caches an environment value, not yet reset. |
| LiveResource | 14 | No genuine leak (see adjudication) — all process-lifetime primitives, completed `Task`s, or input-keyed caches. |
| NeedsReview | 13 | Mostly no (see adjudication) — version/UA caches, stateless singletons, machine-cert factories; **2** minor gaps. |

**Attention surface = 35** (EnvDerivedNotReset + LiveResource + NeedsReview). Adjudicated individually below.

## Why the four big buckets cannot leak

- **ImmutableValue (711)** — `const` and `static readonly` of primitive/string/enum/`Type`/`Guid`/`TimeSpan`/`Uri`
  etc. Their value is fixed at first init and a fresh process computes the identical value. No write path exists.
- **StableSingletonOrTable (421)** — comparers, encodings, regexes, JSON contexts, the generated `Strings.resourceMan`
  localization tables, and constant lookup tables (framework mappings, etc.). Stateless and deterministic; a reused
  process and a fresh process are indistinguishable. (Generated `ResourceManager`s are written once via lazy-init to
  a deterministic value — that is why they are here and not in *NeedsReview*.)
- **InputKeyedCache (173)** — `Dictionary`/`ConcurrentDictionary`/`HashSet`/pools used as memoization keyed purely on
  inputs (e.g. `RestoreCommand._frameworkShortNameCache`, parse caches, `NuGetEnvironment.Cache`*). The value is a
  pure function of the key, so reuse is correct; the only cost in a long-lived host is **memory growth**, not
  wrong results. (*`NuGetEnvironment.Cache` is the one exception — see the env gaps; its values are environment-
  derived paths, so it is listed there too.)
- **ThreadScratch (7)** — `GraphOperations`/`RuntimeGraph` resolver scratch dictionaries/queues marked
  `[ThreadStatic]`. They are cleared at the start of each use and only ever pin a little memory on pooled threads;
  no value survives a use, let alone a build.

## Mitigated buckets (already reset)

**ResetByTraits (2):** `NuGetTraits._instance` / `.Instance` — the single resettable env holder, re-read at the
**start** of restore (from the task environment) and at the **end** of build (cleanup).

**ResetByCleanup (8):** torn down by `RestoreProcessStateCleanup.Reset()` at end of build —
`PluginManager._lazy`/`.Instance`/`_currentProcessId` (disposes plugin **processes + idle timers**),
`ProxyCache._instance`/`.Instance`, `DefaultCredentialServiceUtility.DelegatingLogger`/`_credentialServiceCreatedHere`,
and `HttpHandlerResourceV3.CredentialService`. All ownership-gated so externally owned state (e.g. a VS-set
credential service) is never touched.

## Attention surface — full hand-adjudication (35)

### A. EnvDerivedNotReset (8) — the genuine residual gaps

These cache an environment-derived value on first use and never re-read it, so a process reused across builds keeps
the first build's value. Fix = route the flag/value through `NuGetTraits` (or a sibling reset hook) so it is re-read
at restore start. None affects package-resolution correctness; they affect paths/limits/diagnostics.

| Member | Caches (env) | Why it's the first build's value | Fix |
| --- | --- | --- | --- |
| `SourceRepositoryDependencyProvider._throttle` | `NUGET_CONCURRENCY_LIMIT` → `SemaphoreSlim` | semaphore sized once at type init | size from `NuGetTraits`; recreate on reset |
| `NuGetEnvironment._getHome` | NuGet home (`NUGET_HOME`/profile) | `Lazy<string>` evaluated once | reset the home/temp/`Cache` trio together |
| `NuGetEnvironment._nuGetTempDirectory` | temp dir (`NUGET_PACKAGES`/`TMP`…) | assigned once | reset trio |
| `NuGetEnvironment.Cache` | resolved `NuGetFolderPath`→path | dictionary of env-derived paths, populated lazily | clear on reset |
| `ConcurrencyUtilities._basePath` | `Temp`/lock dir (2nd-order, via `NuGetEnvironment.Temp`) | derived from the temp path above | falls out once `NuGetEnvironment` resets |
| `X509ChainBuildPolicyFactory.Policy` | cert-chain policy env vars | `IX509ChainBuildPolicy` built once | route flags through `NuGetTraits`; rebuild on reset |
| `ExceptionLogger.Instance` | `NUGET_SHOW_STACK` | flag read in ctor at type init | migrate flag to `NuGetTraits` |
| `PluginLogger.DefaultInstance` | `NUGET_PLUGIN_ENABLE_LOG` + log dir + CWD; `IDisposable` | flag read once; holds a log writer when enabled | migrate flag to `NuGetTraits`; dispose on reset |
| `StreamExtensions.Testable.Default` / `ZipArchiveExtensions.Testable.Default` | test-mode seam | test-only; benign in production | low priority |

### B. LiveResource (14) — none are genuine leaks

| Member(s) | What it is | Verdict |
| --- | --- | --- |
| `CommandsEventSource` / `CommonEventSource` / `ConfigurationEventSource` `.Instance` | ETW `EventSource` singleton | **Process-lifetime by design, created once.** See §D — restore is publish-only (`WriteEvent`); it never recreates the singleton nor attaches an `EventListener`, so there is no double-registration. |
| `TaskResult.True/False/Zero/One`, `NullTaskResult.Instance` | cached **completed** `Task<T>` | **Immutable.** A completed task holds no OS resource (matched only because the type is `Task<T>`). |
| `ConcurrencyUtilities.PerFileLock` (`KeyedLock`) | process-wide file-lock coordinator | **Correct to share;** empty between uses; it is the cross-build file-lock primitive. |
| `CredentialService.ProviderSemaphore` (`Semaphore`) | gate around credential providers | released after use; reuse is correct. |
| `HttpSourceAuthenticationHandler._credentialPromptLock`, `ProxyAuthenticationHandler._credentialPromptLock` (`SemaphoreSlim`) | serialize interactive auth prompts | released after use; reuse is correct. |
| `NuGetExtractionFileIO._createFileMethod` (`Lazy<Func<…FileStream>>`) | deterministic file-create method **selector** | picks an impl by runtime; holds no open handle. |
| `NullSourceCacheContext.Instance` | null-object cache context | empty; disposing a no-op. |
| `LegacyFeedCapabilityResourceV2Feed.CachedCapabilities` (`ConcurrentDictionary<url, Task<caps>>`) | V2 feed capabilities by source URL | **input-keyed cache;** values stable per endpoint; memory-only. |

The live OS resources that *do* require disposal — plugin **processes/timers** and HTTP **handlers/sockets** —
are not in this bucket: plugin state is `PluginManager` (**ResetByCleanup**), and the per-source `HttpSource`
handler cache is instance state on provider objects held behind the static `Repository` provider factory, tracked
as the one explicit follow-up in the roadmap (dispose to release sockets).

### C. NeedsReview (13) — mutable globals written on a reachable path

| Member | Adjudication |
| --- | --- |
| `ClientVersionUtility._clientVersion` (`string?`) | Caches this assembly's informational version — **constant per process.** No leak. |
| `MinClientVersionUtility._clientVersion` (`NuGetVersion?`) | Same — assembly version cache. No leak. |
| `NullLogger._instance` (`ILogger?`) | Stateless singleton. No leak. |
| `DefaultCompatibilityProvider._instance` | Stateless framework-compatibility singleton; deterministic mapping. No leak. |
| `JsonSerializationUtilities.Serializer` (`JsonSerializer`) | Stateless Newtonsoft config singleton. No leak. |
| `NullSourceCacheContext._instance` (`SourceCacheContext?`) | Null-object singleton; empty. No leak. |
| `NuGetExtractionFileIO._unixPermissions` (`int`) | `Convert.ToInt32("766", 8)` — a constant; "written" = its initializer. No leak. |
| `NuGetTestMode.Enabled` (`bool`) | Now seeded from `NuGetTraits` at type init; the setter is a **test-only** save/restore helper. Benign in production (frozen `false`). Minor: not re-read after a trait reset — test-only, so acceptable. |
| `X509TrustStore.CodeSigningX509ChainFactory`, `TimestampingX509ChainFactory` (`IX509ChainFactory?`) | Machine **cert-store**-backed factories, idempotently initialized. Machine state, not per-build; a fresh process builds the identical factory. No per-build leak. |
| `UserAgent.UserAgentString` (`string`) | Host sets it once from version info; constant. No leak. |
| `ConcurrencyUtilities._basePath` (`string?`) | **Minor gap** — second-order temp-path cache; listed under the env gaps (A). |
| `HttpSourceResourceProvider.Throttle` (`IThrottle?`) | **Minor gap** — a settable global request throttle a host may set per build; could carry over to a later build that does not set it. Fix: set/reset per build. |

### D. Event subscriptions & double-registration

Static **state** is only half the story: a static/long-lived **event** that restore subscribes to with `+=` on
each invocation, without a matching `-=`, would accumulate duplicate handlers in a reused process — each handler
then fires N times, pins the subscriber (and its object graph) in memory, and can change behavior. This is a real
leak class, so it was audited explicitly (the analyzer records event operations and distinguishes `+=` /
`-=` / raise via `IEventAssignmentOperation.Adds`).

**Result: zero static-event subscriptions and zero `AppDomain` handler registrations are reachable from
`dotnet restore`.** Specifically:

- **NuGet's own static events** — `ProtocolDiagnostics.HttpEvent` / `ResourceEvent` / `NupkgCopiedEvent` /
  `ServiceIndexEntryEvent`. On the restore path only the **publisher** (`ProtocolDiagnostics.RaiseEvent` →
  `evt?.Invoke(...)`) is reachable. The sole subscriber, `PackageSourceTelemetry` in `NuGet.VisualStudio.Common`,
  is Visual-Studio-only (not on the `dotnet restore` path) **and** pairs every `+=` with a `-=` in its `Dispose`.
  So restore **adds no handler** — these event fields keep whatever handler set the host had (none, in a
  restore-only host), and a reused restore host accumulates nothing. (An earlier draft mislabeled these as
  "subscriptions"; they are raises. The analyzer was corrected to separate raise/subscribe/unsubscribe, which is
  why the static-event-subscription sink count is now 0.)
- **ETW `EventSource`s** — `CommonEventSource` / `CommandsEventSource` / `ConfigurationEventSource` are
  `static readonly … Instance = new()` singletons, **created exactly once** at type init. Restore only calls
  `WriteEvent` (publish). It never constructs a second instance and never attaches an `EventListener` /
  `EnableEvents`, so there is no second registration.
- **The only `+=` reachable from restore** are `process.Exited += OnProcessExited` on **per-plugin `Process`
  objects** (`PluginProcess`, `MonitorNuGetProcessExitRequestHandler`). Those are instance subscriptions on
  objects **owned by `PluginManager`**, are paired with `-=` in their handlers, and are torn down when the plugin
  manager is reset/disposed (**ResetByCleanup** — which also kills the plugin processes). No process-global event
  is involved.

Conclusion for events: restore is **publish-only** with respect to every process-global event, so the
double-registration vector does not exist on the restore path; no reset of event handler lists is required.

## Conclusion — what "full resetability" requires

After the stacked PRs, the reachable static surface decomposes into: **1346 members that provably cannot leak
per-build correctness state** (immutable / deterministic / input-keyed / thread-scratch / process-lifetime
primitive / already reset), and a **small, fully enumerated residual of ~11 environment/path/throttle caches**
plus the **one HttpSource handler-cache disposal** follow-up. **Event subscriptions are clean** — restore is
publish-only for every process-global event (§D), so no event-handler reset is needed.

To reach *true* zero, do exactly this (all from the audit above, none structural):

1. Route the env gaps (A) through `NuGetTraits` and reset them with it: the `NuGetEnvironment` home/temp/`Cache`
   trio (+ `ConcurrencyUtilities._basePath`), `SourceRepositoryDependencyProvider._throttle`,
   `X509ChainBuildPolicyFactory.Policy`, `ExceptionLogger`, `PluginLogger` (+ dispose its writer).
2. Reset/`set`-per-build the one mutable global throttle `HttpSourceResourceProvider.Throttle`.
3. Dispose the per-source `HttpSource` handler cache via the `Repository` provider factory (release sockets).

Items 1–2 are mechanical migrations validated by the same analyzer (each drops the residual count); item 3 is the
single piece of new plumbing. Until then, the only observable cross-build effect of a reused restore host is a
**stale environment value or extra retained memory** — never a wrong restore result — and the opt-in reset is off
by default.
