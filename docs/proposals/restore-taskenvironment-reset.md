# Spec (part 2): re-read restore's environment from MSBuild `TaskEnvironment`

- Status: **Draft / for review**
- Builds on: the `NuGetTraits` proposal (`docs/proposals/environment-variable-traits.md`) and the end-of-build
  reset (stacked PRs 2–3).
- Related: dotnet/msbuild#13702 (Enlighten RestoreTask); MSBuild `IMultiThreadableTask` / `TaskEnvironment`.

## 1. Problem

Even after `NuGetTraits` centralizes environment reads (part 1) and we reset it per build (start + end), the reads
still come from the **process** environment (`Environment.GetEnvironmentVariable`). Under **multithreaded MSBuild**
many tasks share one process and one set of process-global state; the safe per-task view is MSBuild's
`TaskEnvironment` (`IMultiThreadableTask.TaskEnvironment`), which also backs `GetAbsolutePath` /
`GetProcessStartInfo` for the CWD/process concerns documented in the earlier CWD analysis. Restore should read its
environment (and resolve paths / spawn processes) through the **task's** environment, not the process's.

## 2. Proposal

1. **Enlighten `RestoreTask`** (and `RestoreTaskEx`): annotate `[MSBuildMultiThreadableTask]` and implement
   `IMultiThreadableTask { TaskEnvironment TaskEnvironment { get; set; } }`.
2. **Feed NuGet's environment from `TaskEnvironment`.** At the start of `Execute` (the seam added in stacked PR 3),
   build an `IEnvironmentVariableReader` over the task environment and reset the traits from it:
   ```csharp
   IEnvironmentVariableReader reader = TaskEnvironment is null
       ? EnvironmentVariableWrapper.Instance
       : new TaskEnvironmentVariableReader(TaskEnvironment.GetEnvironmentVariable);
   NuGetTraits.UpdateFromEnvironment(reader);
   ```
   `TaskEnvironmentVariableReader` (added in this PR) adapts a `Func<string,string?>` to
   `IEnvironmentVariableReader`; the real wiring binds `TaskEnvironment.GetEnvironmentVariable` once that type is
   in the referenced Microsoft.Build.Framework. **No reflection** (per repo guidelines) — the binding is a direct
   method group.
3. **Route the remaining direct reads** through the same reader. Restore already threads
   `IEnvironmentVariableReader` widely; for the multithreaded path, the reader handed in is the
   `TaskEnvironment`-backed one (so `BuildTasksUtility`, settings, plugins, etc. see the task environment).
4. **CWD / process / path APIs** (separate but adjacent): migrate the touchpoints from the CWD analysis
   (`Path.GetFullPath`, `Environment.CurrentDirectory`, `Directory.GetCurrentDirectory`, `ProcessStartInfo`) to
   `TaskEnvironment.GetAbsolutePath` / `TaskEnvironment.GetProcessStartInfo`, per MSBuild's task-authoring
   guidance. This is the larger, mechanical follow-on and is tracked separately.

## 3. What this PR prototypes

- `TaskEnvironmentVariableReader` adapter (builds today; `TaskEnvironment` not referenced yet).
- `NuGetTraits.UpdateFromEnvironment(IEnvironmentVariableReader)` made **public** so a task can supply its own
  environment source.
- `RestoreTask` start-of-restore reset now reads through the task's `_environmentVariableReader` field — the exact
  seam an enlightened task overrides with the `TaskEnvironment`-backed reader.
- Tests for the adapter + traits reset.

The only step that cannot land until MSBuild ships `IMultiThreadableTask`/`TaskEnvironment` is the
`[MSBuildMultiThreadableTask]` annotation + `TaskEnvironment` property on `RestoreTask` and binding
`TaskEnvironment.GetEnvironmentVariable`. Everything else is in place.

## 4. Sequencing & gating

- Gated behind the same opt-in as the reset work (off by default; never affects Visual Studio, which does not run
  `RestoreTask` for its own restore).
- Order: part 1 (`NuGetTraits` + migrate caches) → reset at end (PR 2) → reset at start (PR 3) → read from
  `TaskEnvironment` (this PR) → migrate CWD/path/process APIs to `TaskEnvironment` (follow-on).

## 5. Open questions

- Whether to expose a single `IEnvironmentVariableReader RestoreTask.EnvironmentReader` seam publicly vs keep it
  internal/virtual.
- Exact lifetime: re-read traits once at task start (current) vs per-project; restore is a single whole-graph task
  invocation, so once-at-start is sufficient (see the restore execution-model notes in #13702).
- Coordinating the env-reader threading with the existing `IEnvironmentVariableReader` overloads so the
  `TaskEnvironment`-backed reader reaches all consumers, not just `NuGetTraits`.
