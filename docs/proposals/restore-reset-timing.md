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
- **The catch:** `dotnet build` runs restore and build as **two submissions inside one
  `BuildManager.BeginBuild`/`EndBuild` session**, and `RegisteredTaskObjectLifetime.Build` disposes only at
  `EndBuild`. So the current `RegisterTaskObject(..., Build, ...)` cleanup fires **after the whole build**, not
  between restore and build — holding plugin processes + sockets idle through the (possibly long) compile phase.

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

| Hook | Fires | Pro / Con |
| --- | --- | --- |
| `RegisterTaskObject(…, Build)` (current) | at `EndBuild` — after the **whole build** | Simplest; one teardown per invocation. **Con:** in `dotnet build`, plugin processes + sockets stay alive idle through the entire compile phase (e.g. all of OrchardCore's build). |
| Target with `AfterTargets="Restore"` (gated `Condition="'$(MSBuildIsRestoring)'=='true'"`) that runs the teardown | at the **end of the Restore submission**, right after `RestoreTask`, before the build/compile phase | Frees plugins/sockets as soon as restore is done. Runs on the entry node where the caches live. **Con:** if the same process restores again later it re-spawns plugins (cheap, lazy). |
| End of `RestoreTask.Execute()` itself | immediately after the restore completes (in-proc) | Most direct, no extra target. Same trade-off as above; must be gated/opt-in so a `dotnet build` that immediately needs the caches again isn't penalized (it doesn't — build phase doesn't use them). |

**Guidance:** for a reused host, prefer tearing down **at the end of the Restore submission** (an
`AfterTargets="Restore"` cleanup target, or at the tail of `RestoreTask.Execute`) so plugin processes and sockets
are released before the long build/compile phase — they are provably unused after `RestoreTask`. Keep
`RegisterTaskObject(Build)` as the backstop that also covers the `dotnet restore`-only path (where it already
coincides with end-of-restore) and anything that must survive to `EndBuild`. Either way, correctness is identical;
the choice is purely about how long idle plugin/socket resources are pinned during the build phase.
