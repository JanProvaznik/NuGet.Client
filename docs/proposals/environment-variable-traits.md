# Spec: Centralized, resettable environment-variable handling (`NuGetTraits`)

- Status: **Draft / for review**
- Author: @JanProvaznik
- Related: dotnet/msbuild#13702 (Enlighten RestoreTask); NuGet end-of-build process-state teardown work
- Area: `NuGet.Common` (baseline assembly)

## 1. Summary

Replace NuGet's scattered, per-type **cached static** environment-variable reads (`Lazy<bool>`/`Lazy<string>`
fields, shared-source `NuGetFeatureFlags`, ad-hoc `Environment.GetEnvironmentVariable` calls captured once at
process start) with a single **resettable singleton** that reads all environment-derived settings once into
typed, immutable members — modeled on MSBuild's [`Traits`](https://github.com/dotnet/msbuild/blob/main/src/Framework/Traits.cs)
class. The singleton lives in the lowest NuGet assembly (`NuGet.Common`), is created from an
`IEnvironmentVariableReader`, and can be **recreated** (`UpdateFromEnvironment()` / test reset) so that a process
reused across builds (MSBuild Server / multithreaded MSBuild) and parallel tests observe the correct values.

## 2. Motivation

### 2.1 The problem with cached statics

NuGet reads many environment variables into `static` (often `Lazy<T>`) fields that are evaluated **once per
process** and never re-read. Representative examples (non-exhaustive):

| Location | Member | Env var(s) |
|----------|--------|------------|
| `build/Shared/NuGetFeatureFlags.cs` | `_isSystemTextJsonDeserializationEnabledByEnvironment` (`Lazy<bool>`) | `NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION` |
| `NuGet.Common/PathUtil/NuGetEnvironment.cs` | `_getHome` (`Lazy<string>`), `_nuGetTempDirectory` | `HOME`/`USERPROFILE`/`NUGET_*`, `TMP`/`TEMP` |
| `NuGet.Common/RuntimeEnvironmentHelper.cs` | `_isRunningInVisualStudio` (`Lazy<bool>`) | VS env vars |
| `NuGet.Protocol/Utility/PackageIdValidator.cs` | `IsValidationDisabled` (`Lazy<bool>`) | `NUGET_ID_VALIDATION...` |
| `NuGet.Protocol/.../NuGetTestMode` | `Enabled` (cached) | `NuGet_TestMode` |
| `NuGet.Credentials/PreviewFeatureSettings.cs` | `DefaultCredentialsAfterCredentialProviders` | `NUGET_CREDENTIAL_*` |
| `NuGet.Protocol/EnhancedHttpRetryHelper.cs` | `_retryCount`/`_retry429`/`_observeRetryAfter` | `NUGET_ENHANCED_NETWORK_RETRY*` |

This is fine when the process **dies after each build**, but it is the wrong model for where NuGet is going:

1. **Reused host processes.** Under MSBuild Server and the multithreaded-MSBuild effort, `RestoreTask` runs in a
   process that survives many builds. A value captured at first use never refreshes; the recent end-of-build
   process-state teardown (see dotnet/msbuild#13702) had to special-case resetting `PluginManager`/`ProxyCache`/
   the credential service precisely because these statics leak across builds. Env-derived feature flags have the
   same problem and currently have **no reset path at all**.
2. **Tests.** Environment variables are process-global. Cached statics make a flag's value depend on *test
   execution order* (whoever triggers the `Lazy` first wins) and prevent parallel tests from overriding it. The
   existing guidance in `docs/coding-guidelines.md` ("Getting or Setting Environment Variables") already calls
   this out and mandates `IEnvironmentVariableReader`, but the **static** caches bypass that mechanism.
3. **Per-assembly duplication.** `NuGetFeatureFlags` is **shared source** compiled into many assemblies, so each
   assembly gets its *own* cache of the same flag — N independent copies that can't be reset coherently.

### 2.2 Why `IEnvironmentVariableReader` alone is not enough

The current guideline (DI an `IEnvironmentVariableReader`, with `internal` overloads for tests) is correct for
*reading* a variable, and this proposal keeps and builds on it. But it does not, by itself:
- give a single place that owns the **typed, parsed** value of each flag (every call site re-parses),
- provide a **reset** semantic for reused processes,
- remove the **static caches** that the DI pattern was meant to replace but in practice coexists with.

## 3. Goals / Non-goals

**Goals**
- One typed, discoverable home for environment-derived engine settings.
- Values read through `IEnvironmentVariableReader` (keeps the testability contract).
- A **reset** that re-reads the environment, usable by (a) the end-of-build cleanup in reused hosts and (b) tests.
- Remove per-type/per-assembly static env caches in favour of the singleton.
- No behavioural change for the normal "process dies after build" path.

**Non-goals**
- Changing *which* environment variables exist or their semantics.
- Replacing `IEnvironmentVariableReader` (it stays; `NuGetTraits` is a consumer of it).
- Centralizing non-environment process-global state (proxy cache, plugins, credential service) — that is the
  separate teardown work; this spec only covers env-derived settings.
- Machine-invariant facts that happen to live next to env reads (e.g. OS detection in `RuntimeEnvironmentHelper`
  `_isWindows/_IsLinux/_IsMacOSX`) — these never change within a machine and may stay as-is.

## 4. Prior art: MSBuild `Traits`

`Microsoft.Build.Framework.Traits` (internal, in the lowest framework assembly):

```csharp
internal class Traits
{
    private static Traits _instance = new Traits();
    public static Traits Instance => BuildEnvironmentState.s_runningTests ? new Traits() : _instance;

    public Traits() { /* read every env var ONCE into readonly fields */ }

    public readonly bool ForceMultiThreaded = Environment.GetEnvironmentVariable("MSBUILDFORCEMULTITHREADED") == "1";
    public readonly int  CopyTaskParallelism = EnvironmentUtilities.GetValueAsInt32OrDefault("MSBUILDCOPYTASKPARALLELISM", -1);
    public EscapeHatches EscapeHatches { get; }   // nested grouping of legacy toggles
    // ...

    public static void UpdateFromEnvironment() => _instance = new Traits(); // resettable
}
```

Salient properties we adopt:
- **Single instance**, typed `readonly` members read once in the constructor.
- **`UpdateFromEnvironment()`** recreates the instance to pick up a changed environment.
- **Test behaviour**: when running tests, `Instance` returns a *fresh* `Traits` each access so tests see current
  env without cross-test bleed.
- **Nested grouping** (`EscapeHatches`) keeps the surface organized.

Differences for NuGet (below): read via `IEnvironmentVariableReader` (NuGet's testability contract) rather than
`Environment.*` directly; explicit reset hook wired into the end-of-build cleanup.

## 5. Proposed design

### 5.1 Location: `NuGet.Common`

`NuGet.Common` is the correct baseline: it depends only on `NuGet.Frameworks` + the BCL, **everything else depends
on it**, and it already hosts `IEnvironmentVariableReader` and `EnvironmentVariableWrapper`.

Caveat: `NuGet.Frameworks` and `NuGet.Versioning` sit *below* `NuGet.Common` and could not consume `NuGetTraits`.
Today neither reads environment variables, so this is acceptable; if that ever changes we would need an even
lower shared assembly (out of scope).

### 5.2 Shape

```csharp
namespace NuGet.Common
{
    public sealed class NuGetTraits
    {
        private static NuGetTraits _instance = new NuGetTraits(EnvironmentVariableWrapper.Instance);

        /// <summary>The process-wide traits. In production this is cached; recreate via <see cref="UpdateFromEnvironment"/>.</summary>
        public static NuGetTraits Instance => _instance;

        // Test/prod construction goes through the reader (NuGet testability contract).
        internal NuGetTraits(IEnvironmentVariableReader env)
        {
            Http = new HttpTraits(env);
            FeatureFlags = new FeatureFlagTraits(env);
            Paths = new PathTraits(env);
            Diagnostics = new DiagnosticTraits(env);
        }

        public HttpTraits Http { get; }
        public FeatureFlagTraits FeatureFlags { get; }
        public PathTraits Paths { get; }
        public DiagnosticTraits Diagnostics { get; }

        /// <summary>Recreate the shared instance from the current environment. Intended for hosts that reuse the
        /// process across builds (e.g. end-of-build cleanup) and for tests.</summary>
        public static void UpdateFromEnvironment() => UpdateFromEnvironment(EnvironmentVariableWrapper.Instance);

        internal static void UpdateFromEnvironment(IEnvironmentVariableReader env)
            => Volatile.Write(ref _instance, new NuGetTraits(env));
    }

    public sealed class FeatureFlagTraits
    {
        internal FeatureFlagTraits(IEnvironmentVariableReader env)
        {
            UseSystemTextJsonDeserialization =
                string.Equals(env.GetEnvironmentVariable("NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION"), "true", StringComparison.OrdinalIgnoreCase);
            // ... other feature flags
        }

        public bool UseSystemTextJsonDeserialization { get; }
    }
    // HttpTraits, PathTraits, DiagnosticTraits analogous.
}
```

Notes:
- `Instance` is read through a `Volatile` field; `UpdateFromEnvironment` swaps a fully-constructed immutable
  instance (publication is atomic). Members are `get`-only and set once in the constructor, so no per-member
  locking is needed.
- The `internal` reader-accepting constructor preserves the existing test contract; production uses the public
  parameterless path (`EnvironmentVariableWrapper.Instance`).
- Grouping (`Http`/`FeatureFlags`/`Paths`/`Diagnostics`) mirrors MSBuild's `EscapeHatches` nesting and keeps the
  surface navigable.

### 5.3 Consumption

Before:
```csharp
// build/Shared/NuGetFeatureFlags.cs (compiled into many assemblies)
if (NuGetFeatureFlags.IsSystemTextJsonDeserializationEnabledByEnvironment()) { ... }
```
After:
```csharp
if (NuGetTraits.Instance.FeatureFlags.UseSystemTextJsonDeserialization) { ... }
```

### 5.4 Reset integration

- **Reused hosts**: the end-of-build cleanup token registered via `IBuildEngine4.RegisterTaskObject(Build)`
  (the teardown work for dotnet/msbuild#13702) calls `NuGetTraits.UpdateFromEnvironment()` in its `Dispose()`,
  so the next build re-reads the environment — matching "as if the process had restarted". Ownership/gating is
  identical to the rest of that teardown (off by default, opt-in for the reused-process scenario, never affects
  Visual Studio's process).
- **Tests**: a test reset helper (e.g. `NuGetTraits.UpdateFromEnvironment(IEnvironmentVariableReader)` exposed to
  test assemblies, or an `IDisposable` scope) sets a mock reader and restores afterward. Optionally adopt
  MSBuild's "running tests → fresh instance per access" behaviour via a test-only switch.

### 5.5 Relationship to `IEnvironmentVariableReader`

`IEnvironmentVariableReader` remains the single read mechanism and the documented pattern for *raw,
not-feature-flag* reads (e.g. a one-off variable used in a specific component). `NuGetTraits` is the home for
**typed engine settings/feature flags** that were previously cached in statics. The coding-guidelines section
"Getting or Setting Environment Variables" should be updated to say: feature flags / engine toggles go through
`NuGetTraits`; component-specific reads continue to DI an `IEnvironmentVariableReader`.

## 6. Inventory & migration

### 6.1 To migrate into `NuGetTraits` (env-derived, currently cached)
- `NuGetFeatureFlags.UseSystemTextJsonDeserialization` (shared source → single home).
- `NuGetTestMode.Enabled`.
- `PackageIdValidator.IsValidationDisabled`.
- `PreviewFeatureSettings.*`.
- `EnhancedHttpRetryHelper` retry settings (already reader-injected at instance level — fold the *defaults-from-env*
  into `NuGetTraits.Http`, keep per-request overrides where they belong).
- `NuGetEnvironment` env-derived `Home`/temp directory (evaluate carefully — see §8).
- `RuntimeEnvironmentHelper._isRunningInVisualStudio` (env-derived).

### 6.2 Stays as-is
- `RuntimeEnvironmentHelper` OS detection (`_isWindows/_IsLinux/_IsMacOSX/_isMono`) — machine-invariant.
- `CryptoHashUtility.AllowFipsAlgorithmsOnly` — config/registry, not env (separate concern).
- Component-specific one-off env reads that already DI a reader and are not process-cached.

### 6.3 Back-compat
- Keep the existing public entry points (e.g. `NuGetFeatureFlags`, `PackageIdValidator.IsValidationDisabled`)
  as thin shims that delegate to `NuGetTraits.Instance` for one release, to avoid a big-bang call-site change and
  to keep any external callers working. Mark shims `[Obsolete]` where they are public.
- No behavioural change on the normal path: first read produces identical values to today.

## 7. Public API & layering considerations

- **Public vs internal.** MSBuild's `Traits` is `internal` + `InternalsVisibleTo`. NuGet conventionally exposes
  cross-assembly capability via **public** API (NuGet.Common has almost no production `InternalsVisibleTo`).
  Recommendation: make the **accessor and typed property surface public** (`NuGetTraits.Instance.Http.Foo`) but
  treat individual flags as non-contractual engine toggles (documented as such), and keep raw env var **names**
  `internal`. Adding `NuGetTraits` requires `PublicAPI.Unshipped.txt` entries in `NuGet.Common` (per-TFM where
  applicable).
- **AOT / trimming / feature switches.** `NuGetFeatureFlags` also carries a `[FeatureSwitchDefinition]` AppContext
  switch (`NuGet.UseSystemTextJsonDeserialization`) for trimming. `NuGetTraits` must preserve the AppContext
  switch path (compile-time/trimming) *and* the env-var path; the AppContext switch is not resettable and takes
  precedence where defined. Document the precedence (AppContext switch > env var > default).
- **`#pragma warning disable RS0030`**: direct `Environment.*` is banned by analyzer; `NuGetTraits` reads only via
  `IEnvironmentVariableReader`, so it does not need the suppression.

## 8. Open questions

1. **Naming**: `NuGetTraits` vs `Traits` vs `EnvironmentTraits`. (`Traits` collides conceptually with MSBuild;
   `NuGetTraits` is unambiguous.)
2. **Public vs internal** surface (see §7) — needs an API-review decision.
3. **`NuGetEnvironment` home/temp paths**: these are read very early and widely; folding them into `NuGetTraits`
   may be a larger, riskier change than the boolean flags. Proposal: migrate boolean/int feature flags first;
   handle path discovery in a follow-up.
4. **Test reset ergonomics**: explicit `UpdateFromEnvironment(reader)` vs an `IDisposable` scope vs MSBuild's
   "fresh per access while testing". Prefer an explicit scope to avoid a hot-path branch in `Instance`.
5. **Reset granularity**: a single `UpdateFromEnvironment()` recreates everything. Is per-category reset ever
   needed? (Probably not.)

## 9. Risks

- **Behavioural drift** if a flag's parsing differs after centralization — mitigate with characterization tests
  per migrated flag (same input env → same value).
- **Ordering/visibility**: a consumer that captured `NuGetTraits.Instance` in its *own* static would re-introduce
  the cache. Guidance: always read `NuGetTraits.Instance.X` at point of use, never cache the instance.
- **Layering**: anything below `NuGet.Common` cannot use it (accepted, §5.1).
- **Public API commitment** if exposed publicly (§7).

## 10. Rollout

1. Add `NuGetTraits` + categories in `NuGet.Common` (+ tests, PublicAPI entries). No call-site changes.
2. Migrate `NuGetFeatureFlags` (shared source) to delegate to `NuGetTraits`; update call sites.
3. Migrate the remaining boolean/int flags (TestMode, PackageIdValidator, PreviewFeatureSettings, retry helper
   defaults); leave `[Obsolete]` shims.
4. Wire `NuGetTraits.UpdateFromEnvironment()` into the end-of-build cleanup; add reused-process tests.
5. (Follow-up) Evaluate `NuGetEnvironment` path discovery.
6. Update `docs/coding-guidelines.md` "Getting or Setting Environment Variables".

## Appendix A — references
- MSBuild `Traits`: https://github.com/dotnet/msbuild/blob/main/src/Framework/Traits.cs
- NuGet env-var guideline: `docs/coding-guidelines.md` → "Getting or Setting Environment Variables"
- Existing pattern being generalized: `build/Shared/NuGetFeatureFlags.cs`
- Process-reuse teardown context: dotnet/msbuild#13702

## Appendix B — cached env-var call sites (analyzer-generated)

The complete inventory below was produced by an interprocedural, cross-assembly analyzer that flags **static
fields/properties whose value is derived from an environment variable** (directly, via a `Lazy<T>` factory, or via
a `??=`/static-ctor cache assignment), following calls into helpers. It is more accurate than grep: it ignores
per-call reads that correctly use `IEnvironmentVariableReader` (they re-read and are fine), and it excludes
static state that *looks* env-derived but is not — e.g. `RuntimeEnvironmentHelper._isRunningInVisualStudio`
(process-name based) is correctly **not** listed.

Two shapes appear: **(F)** a flag/value cached from env (the primary migration target), and **(S)** a singleton
whose *constructor/helper* reads env as a side effect of construction (migrate by routing that read through
`NuGetTraits`, or accept). 15 members across the regular `dotnet restore` closure (`NuGet.Clients`/CLI not
included — follow-up):

`NuGet.Common`
- (F) [`NuGetEnvironment._getHome`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Common/PathUtil/NuGetEnvironment.cs#L27) `Lazy<string>` — via `GetHome` (`HOME`/`USERPROFILE`/`DOTNET_CLI_HOME`)
- (F) [`NuGetEnvironment._nuGetTempDirectory`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Common/PathUtil/NuGetEnvironment.cs#L29) `string` — assigned via `GetNuGetTempDirectory` (`NUGET_SCRATCH`)
- (F) [`ConcurrencyUtilities._useDeleteOnClose`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Common/ConcurrencyUtilities.cs#L22) `bool?` — assigned-from-env (`NUGET_ConcurrencyUtils_DeleteOnClose`)
- (S) [`ExceptionLogger.Instance`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Common/Logging/ExceptionLogger.cs#L39) — via ctor (reads a debug env var)

`build/Shared`
- (F) [`NuGetFeatureFlags._isSystemTextJsonDeserializationEnabledByEnvironment`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/build/Shared/NuGetFeatureFlags.cs#L15) `Lazy<bool>` — `NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION` (compiled into *many* assemblies)

`NuGet.Protocol`
- (F) [`NuGetTestMode.Enabled`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Protocol/NuGetTestMode.cs#L19) `bool` — static-ctor assigned via `FromEnvironmentVariable` (`NuGetTestModeEnabled`)
- (F) [`PackageIdValidator.IsValidationDisabled`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Protocol/Utility/PackageIdValidator.cs#L15) `Lazy<bool>` — via `IsPackageIdValidationDisabled`
- (S) [`PluginManager._lazy`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Protocol/Plugins/PluginManager.cs#L28) — via ctor (plugin-path env)
- (S) [`PluginLogger.DefaultInstance`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Protocol/Plugins/Logging/PluginLogger.cs#L20) — via ctor (plugin-log env)

`NuGet.Credentials`
- (F) [`PreviewFeatureSettings.DefaultCredentialsAfterCredentialProviders`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Credentials/PreviewFeatureSettings.cs#L26) `bool` — via `GetFlagFromEnvironmentVariable`

`NuGet.Packaging`
- (F) [`X509ChainBuildPolicyFactory.Policy`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Packaging/Signing/ChainBuilding/X509ChainBuildPolicyFactory.cs#L18) — assigned via an `IEnvironmentVariableReader` read
- (S) [`StreamExtensions.Testable.Default`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Packaging/PackageExtraction/StreamExtensions.cs#L71) — via ctor (test-hook env)
- (S) [`ZipArchiveExtensions.Testable.Default`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Packaging/PackageExtraction/ZipArchiveExtensions.cs#L81) — via ctor (test-hook env)

`NuGet.ProjectModel`
- (F) [`DependencyGraphSpec.UseLegacyHashFunction`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.ProjectModel/DependencyGraphSpec.cs#L25) `bool?` — assigned-from-env (`NUGET_ENABLE_LEGACY_DGSPEC_HASH_FUNCTION`)

`NuGet.Commands`
- (S) [`SourceRepositoryDependencyProvider._throttle`](https://github.com/JanProvaznik/NuGet.Client/blob/8119f5dc403909784b3c8251ec90beb945e2f4ef/src/NuGet.Core/NuGet.Commands/RestoreCommand/SourceRepositoryDependencyProvider.cs#L44) `SemaphoreSlim` — via `GetThrottleSemaphoreSlim` (concurrency env)

The migration target is the **(F)** members (and the env reads inside the **(S)** constructors): each becomes a
typed member of `NuGetTraits`, read once via `IEnvironmentVariableReader` and refreshable via
`UpdateFromEnvironment()`.

