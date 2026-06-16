# Can restore state-resetability avoid new public API?

- Status: **Design note** (companion to the resetability roadmap)
- Question: *Is there a way to achieve the static-state resetability without changing public API?*

## Short answer

**Not literally zero new public API — but it can be reduced from the current ~12 entries to a single small public
seam (~1 type, ~2 members) in `NuGet.Common`.** The constraint is NuGet's own policy, not a technical limit.

## Why "internal + InternalsVisibleTo" is off the table

The natural way to avoid public API would be to make `NuGetTraits` and the `Reset*` methods `internal` and grant
`[InternalsVisibleTo]` to the consuming runtime assemblies. NuGet's coding guidelines explicitly forbid this
(`docs/coding-guidelines.md`):

> InternalsVisibleTo is used only to allow a unit test to test internal types and members of its runtime
> assembly. **We do not use InternalsVisibleTo between two runtime assemblies.** If two runtime assemblies need to
> share common helpers then we use shared compilation.

Every existing `InternalsVisibleTo` in `NuGet.Core` targets a *test* assembly — there is no product-to-product IVT
precedent. So the sanctioned cross-assembly mechanisms are: **public API** or **shared compilation** (linked
source, like `build/Shared/`).

## Why shared compilation alone can't do it

Shared compilation duplicates the *source* into each assembly, so each assembly gets its **own** copy of any
`static` — i.e. its own independent cache. That is fine for stateless *helpers*, but a resettable singleton is
shared *state*: a coordinator in `NuGet.Build.Tasks` cannot reach the copy compiled into `NuGet.Protocol`. And the
active teardown we need for parity (dispose plugin **processes/sockets** at end of build) is not a lazy recompute —
it must be actively invoked across the assembly boundary. Crossing that boundary, with IVT and reflection both off
the table, requires **public** visibility somewhere.

Conclusion: cross-assembly reset coordination inherently needs *some* public surface in a low assembly. Zero is not
achievable under the guidelines.

## What the current design exposes (~12 entries)

```
NuGet.Common.NuGetTraits                                   (public type + 6 bool props + Instance + 2 Update*)
NuGet.Configuration.ProxyCache.ResetSharedInstance()
NuGet.Credentials.DefaultCredentialServiceUtility.ResetDefaultCredentialService()
NuGet.Protocol.Plugins.PluginManager.ResetSharedInstance()
```

## Minimal-public-API alternative (~1 type, ~2 members)

Collapse everything behind **one** public seam in `NuGet.Common` (the lowest assembly) and keep all the actual
state and teardown logic `internal` to its own assembly:

```csharp
// NuGet.Common — the only new public API.
public static class NuGetProcessState
{
    // Owning assemblies register an internal teardown (Action is a public BCL type, so no API leak).
    public static void Register(Action reset);
    // Coordinator (end of build) triggers all registered resets.
    public static void Reset();
}
```

- **Env/flag caches** (`NuGetTraits`-style, `NuGetTestMode`, `PackageIdValidator`, `ConcurrencyUtilities`,
  `DependencyGraphSpec`, …): stay `internal`. Each registers a tiny reset delegate (or reads a public *epoch*
  counter and recomputes lazily). No per-flag public API; `NuGetTraits` need not be public.
- **Active teardowns** (`PluginManager`, `ProxyCache`, credential service): stay `internal`; each registers its
  disposal callback with `NuGetProcessState` the first time it creates the shared instance. The `Reset*` methods
  need not be public.
- **Registration** uses a public method taking a public `Action`; the delegates themselves are internal. No
  product-to-product IVT, no reflection.
- The end-of-build hook (`RegisterTaskObject(Build)`) calls `NuGetProcessState.Reset()`.

Trade-off: this adds one indirection (a registry + callbacks) and module-init/first-use registration timing to
reason about, in exchange for shrinking the maintained public surface from ~12 members across four assemblies to
~2 members in one. It does **not** reduce correctness or change the parity behavior.

## Recommendation

If the goal is "smallest possible public API," adopt the single `NuGetProcessState` seam and keep `NuGetTraits`
and all `Reset*` methods internal to their assemblies. If the goal is "discoverable, individually testable
building blocks," the current per-component public `Reset*` methods are simpler to follow. Either way, **some**
public API is required by NuGet's no-product-IVT policy; the realistic target is *minimal*, not *zero*.
