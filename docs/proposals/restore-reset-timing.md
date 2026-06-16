# When to invalidate restore's process state (MSBuild submission model)

- Status: **Design note** (companion to the resetability roadmap)
- Question: *In a normal `dotnet build` (not just `dotnet restore`), do NuGet tasks run before/after the `Restore`
  target, and what is the correct point to invalidate restore's caches in a reused host?*

## TL;DR

- **Before restore:** nothing — `Restore` is the first thing that runs. *Within* restore, the dependency-graph
  collection tasks run on every project, then **`RestoreTask` runs once, last**, on the entry node (it is what
  creates the plugin processes, `HttpSource` handlers, credential service, proxy cache, env traits).
- **After restore:** yes, one NuGet task runs in the build phase — **`GetReferenceNearestTargetFrameworkTask`**
  (project-reference TFM resolution), and the SDK may invoke `CollectPackageReferences`. **Neither touches the
  network/plugin/credential caches** — they are pure `NuGet.Frameworks` / item math.
- So the restore caches are **dormant for the entire build phase**, and resetting them is correctness-safe **any
  time after `RestoreTask` finishes**.
- `dotnet build` runs restore and build as **two submissions inside one `BuildManager.BeginBuild`/`EndBuild`
  session**, and `RegisteredTaskObjectLifetime.Build` disposes only at `EndBuild`. So the current
  `RegisterTaskObject(..., Build, ...)` cleanup fires **at the end of the whole build**.
- **This is exactly parity** with the previous behavior. Before, the build process **died after each build**, so it
  held the plugin processes, sockets, and every other piece of state from restore *through the entire build* and
  only reclaimed them at process exit — i.e. at the same point `EndBuild` represents. **Freeing plugins after the
  build is therefore sufficient** for a reused host to match the old "fresh process per build" semantics. Tearing
  down earlier (at the end of the Restore submission) is an *optional, beyond-parity* optimization, not a
  requirement.

## The execution model (verified against MSBuild source)

`dotnet build` (and `msbuild -restore`) does, in one process:

1. `BuildManager.BeginBuild(parameters)` — **once**.
2. **Restore submission** (`XMake.ExecuteRestore`): builds only the `Restore` target, in a *separate evaluation
   context* — it sets `MSBuildIsRestoring=true` and a unique `MSBuildRestoreSessionId`, and uses
   `BuildRequestDataFlags.ClearCachesAfterBuild` so the projects are re-evaluated from disk for the build (restore
   can change the import graph). Inside this submission:
   - the graph-collection targets/tasks run on **each project node** (`GetRestoreProjectStyleTask`,
     `GetRestoreSettingsTask`, `GetReferenceNearestTargetFrameworkTask` for restore-side P2P, the
     `Collect*`/`_GenerateRestoreGraph*` targets), then
   - **`RestoreTask` runs once, last**, on the entry node — the actual restore.
3. **Build submission** (`ExecuteBuild`/`ExecuteGraphBuild`): the real build (`Build`/`Compile`/…). The only NuGet
   task here is `GetReferenceNearestTargetFrameworkTask` (P2P resolution); `CollectPackageReferences` may be
   pulled in by SDK targets. Cache-irrelevant.
4. `BuildManager.EndBuild()` — **once**. `RegisteredTaskObjectLifetime.Build` objects are disposed **here**.

`RegisteredTaskObjectLifetime` has only `Build` (disposed at `EndBuild`) and `AppDomain` — there is **no
per-submission lifetime**. So you cannot get "after restore but before build" teardown from `RegisterTaskObject`.

`dotnet restore` (restore-only, `-t:Restore`) is just step 1→2→4 with no build submission: there the end of the
restore submission and `EndBuild` coincide, so `RegisterTaskObject(Build)` already fires right after restore.

## OrchardCore-scale note

Even with `/m`, restore runs **`RestoreTask` exactly once** on the entry node — the per-project `Get*` tasks only
gather the graph on worker nodes; the consolidated restore happens once. So for an ~150-project solution there is
**one** set of restore caches (one pool of plugin processes, one `HttpSource` handler set, etc.) to invalidate per
`dotnet build`, regardless of node count. The only question is *when*.

## Recommendation

The goal is **parity** with the old "process dies after each build" model, which held all state from restore
through the whole build and released it at process exit. The hook that matches that point is end-of-build.

| Hook | Fires | Parity? |
| --- | --- | --- |
| `RegisterTaskObject(…, Build)` (current, **recommended**) | at `EndBuild` — after the **whole build**, the same point the old process exited | **Exact parity.** Simplest; one teardown per invocation. Covers the `dotnet restore`-only path too (there end-of-restore == `EndBuild`). |
| Target with `AfterTargets="Restore"` (gated `Condition="'$(MSBuildIsRestoring)'=='true'"`) | at the **end of the Restore submission**, before the build/compile phase | Beyond parity — releases plugins/sockets *earlier* than the old process did. Optional optimization, not required; a later restore in the same process re-spawns plugins (cheap, lazy). |
| End of `RestoreTask.Execute()` itself | immediately after restore completes (in-proc) | Same as above — beyond parity. |

**Guidance:** **freeing plugins (and the other restore caches) after the build is sufficient** and is the
parity-preserving choice, so keep the current `RegisterTaskObject(…, Build)` teardown at `EndBuild`. The old
process held those plugin processes/sockets idle through the entire build anyway, so holding them until `EndBuild`
matches the previous behavior exactly — no earlier teardown is needed. The `AfterTargets="Restore"` hook is only
worth considering as a *separate, opt-in optimization* if profiling shows idle plugin/socket retention during the
compile phase is a real cost; it is a behavior change beyond parity, not part of achieving it.

