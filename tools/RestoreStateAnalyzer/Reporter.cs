using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RestoreStateAnalyzer;

internal sealed class Reporter
{
    private readonly CompilationSet _set;
    private readonly Analyzer _an;
    private readonly (string type, string task, string group)[] _entryTasks;

    private readonly Dictionary<string, string?> _parent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _reachableByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string task, string group)> _entryById;

    public Reporter(CompilationSet set, Analyzer an, (string type, string task, string group)[] entryTasks)
    {
        _set = set;
        _an = an;
        _entryTasks = entryTasks;
        _entryById = an.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        ComputePaths();
        ComputePerTaskReachability();
    }

    private void ComputePaths()
    {
        var q = new Queue<string>();
        foreach (var id in _an.Entries.Keys)
        {
            if (_parent.TryAdd(id, null)) { q.Enqueue(id); }
        }
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (!_an.Edges.TryGetValue(cur, out var nexts)) { continue; }
            foreach (var n in nexts)
            {
                if (_parent.TryAdd(n, cur)) { q.Enqueue(n); }
            }
        }
    }

    private void ComputePerTaskReachability()
    {
        foreach (var grp in _an.Entries.GroupBy(e => e.Value.task))
        {
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var q = new Queue<string>();
            foreach (var e in grp) { if (reachable.Add(e.Key)) { q.Enqueue(e.Key); } }
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!_an.Edges.TryGetValue(cur, out var nexts)) { continue; }
                foreach (var n in nexts) { if (reachable.Add(n)) { q.Enqueue(n); } }
            }
            _reachableByTask[grp.Key] = reachable;
        }
    }

    private List<string> ReachingTasks(string hostId)
    {
        var tasks = new List<string>();
        foreach (var (task, set) in _reachableByTask)
        {
            if (set.Contains(hostId)) { tasks.Add(task); }
        }
        tasks.Sort(StringComparer.Ordinal);
        return tasks;
    }

    private string PathString(string hostId, int cap = 16)
    {
        if (!_parent.ContainsKey(hostId))
        {
            return _an.Display.TryGetValue(hostId, out var d0) ? d0 + "  (host)" : hostId;
        }
        var chain = new List<string>();
        string? cur = hostId;
        var guard = new HashSet<string>(StringComparer.Ordinal);
        while (cur is not null && guard.Add(cur))
        {
            chain.Add(_an.Display.TryGetValue(cur, out var d) ? d : cur);
            cur = _parent.TryGetValue(cur, out var p) ? p : null;
        }
        chain.Reverse();
        if (chain.Count > cap)
        {
            var head = chain.Take(cap - 1).ToList();
            head.Add($"... (+{chain.Count - cap + 1} frames) -> {chain[^1]}");
            chain = head;
        }
        return string.Join("\n      -> ", chain);
    }

    public string Build(int totalErr)
    {
        var sb = new StringBuilder();
        var sinks = _an.Sinks;
        var stat = _an.StaticState;

        sb.AppendLine("# Restore static / lingering process-state reachability report");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("- Target: `NuGet.Build.Tasks` built for **net10.0** (the CoreCLR `dotnet restore` runtime); library dependencies built for **net8.0**.");
        sb.AppendLine("- Source: compiler invocations reconstructed from the real MSBuild binary log (exact csc command lines), so references, `#define`s (`IS_CORECLR`), nullable and langversion match the shipping build precisely.");
        sb.AppendLine($"- Reconstructed compilations: **{_set.Projects.Count}** assemblies, **{_set.Projects.Sum(p => p.Trees.Count)}** source files, **{totalErr}** binding errors.");
        sb.AppendLine($"- Reachable methods / static-init nodes: **{_an.Display.Count}**.");
        sb.AppendLine();

        sb.AppendLine("## What is analyzed");
        sb.AppendLine();
        sb.AppendLine("A whole-program, **cross-assembly interprocedural** walk starting at the MSBuild task entry points. From each `Execute()`/ctor/`Cancel()`/`Dispose()`/property accessor it follows every invocation, object creation, property/event access and static initializer into the *source* of every referenced NuGet assembly. Virtual/interface calls are expanded to all source overrides/implementations. For every reachable method it records:");
        sb.AppendLine();
        sb.AppendLine("- **Static state**: non-const static fields & static properties (classified mutable / readonly-mutable-ref / readonly-immutable / `[ThreadStatic]` / `ThreadLocal`·`AsyncLocal` / `Lazy`).");
        sb.AppendLine("- **Lingering process state sinks**: child-process creation, environment-variable edits/reads, current-directory changes, `Console` redirection, culture changes, registry writes, `AppContext` switches, thread-pool/network global config, `AppDomain` handlers, static event subscriptions, `Environment.Exit`.");
        sb.AppendLine();
        sb.AppendLine("**Accuracy notes**");
        sb.AppendLine();
        sb.AppendLine("- Cross-assembly calls are resolved into source via Roslyn `CompilationReference`s wired exactly as the real build wired them; symbols resolve to the defining assembly's syntax, so bodies in *every* NuGet assembly are walked.");
        sb.AppendLine("- Virtual / interface / abstract calls are expanded to **all** source overrides & implementations, transitively (interface member → abstract base → concrete override). This is a sound over-approximation: it can include a target that a given run would not pick, but it will not miss a statically-possible target.");
        sb.AppendLine("- Conditional compilation is honoured exactly (e.g. `IS_CORECLR`, `NET8_0_OR_GREATER`). Code excluded for the analyzed TFM is **not** reported (e.g. `PluginDiscoverer.IsExecutable`'s `Process` is `#if !NET8_0_OR_GREATER` and so is absent here; it *would* appear on the .NET Framework / older-TFM build).");
        sb.AppendLine("- Lambdas, local functions and `async` state machines are followed (their operations live in the enclosing method body). Pure reflection / dynamically-built delegates are not (NuGet restore does not use reflection dispatch on these paths).");
        sb.AppendLine("- Reads of *framework* static readonly fields are treated as noise and omitted; NuGet-owned static state is reported in full.");
        sb.AppendLine();

        // Entry points
        sb.AppendLine("## Entry points (restore task surface)");
        sb.AppendLine();
        sb.AppendLine("| Task | Group | Resolved |");
        sb.AppendLine("|------|-------|----------|");
        var nbt = _set.ByAssemblyName["NuGet.Build.Tasks"];
        foreach (var (type, task, group) in _entryTasks)
        {
            bool ok = nbt.GetTypeByMetadataName(type) is not null;
            sb.AppendLine($"| `{task}` | {group} | {(ok ? "yes" : "**NO**")} |");
        }
        sb.AppendLine();
        sb.AppendLine("> The `regular` group is what runs for a plain `dotnet restore` (the `RestoreTask` plus the DG-spec collection tasks `NuGet.targets` invokes). The `static-graph` group runs only when `RestoreUseStaticGraphEvaluation=true`.");
        sb.AppendLine();

        // Reconstruction quality
        sb.AppendLine("## Reconstructed assemblies");
        sb.AppendLine();
        sb.AppendLine("| Assembly | Source files | Binding errors |");
        sb.AppendLine("|----------|-------------:|---------------:|");
        foreach (var p in _set.Projects.OrderBy(p => p.AssemblyName))
        {
            int e = p.Compilation!.GetDiagnostics().Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            sb.AppendLine($"| {p.AssemblyName} | {p.Trees.Count} | {e} |");
        }
        sb.AppendLine();

        // SINKS
        sb.AppendLine("## Lingering process-state sinks reachable during restore");
        sb.AppendLine();
        BuildSinkSummary(sb, sinks);

        foreach (var grp in sinks.GroupBy(s => s.finding.Category).OrderBy(g => g.Key.ToString()))
        {
            sb.AppendLine($"### {grp.Key} ({grp.Count()})");
            sb.AppendLine();
            foreach (var (hostId, f) in grp.OrderBy(x => x.finding.Api).ThenBy(x => x.finding.Location))
            {
                var tasks = ReachingTasks(hostId);
                sb.AppendLine($"- **{f.Api}**  — `{f.Location}`");
                sb.AppendLine($"  - in `{f.EnclosingMethod}` ({f.EnclosingAssembly})");
                sb.AppendLine($"  - reachable from tasks: {FormatTasks(tasks)}");
                sb.AppendLine($"  - example path:\n      -> {PathString(hostId)}");
            }
            sb.AppendLine();
        }

        // STATIC STATE
        sb.AppendLine("## Static state reachable during restore");
        sb.AppendLine();
        BuildStaticSummary(sb, stat);

        var ordered = stat.OrderBy(kv => kv.Value.DeclaringAssembly).ThenBy(kv => kv.Value.DeclaringType).ThenBy(kv => kv.Value.Member);
        var highKinds = new HashSet<StaticStateKind>
        {
            StaticStateKind.MutableStaticField, StaticStateKind.ThreadStaticField,
            StaticStateKind.ThreadLocalOrAsyncLocal, StaticStateKind.LazyStaticField,
            StaticStateKind.StaticReadonlyMutableRef, StaticStateKind.MutableStaticProperty,
        };

        foreach (var kindGrp in stat.Values.GroupBy(s => s.Kind).OrderBy(g => g.Key.ToString()))
        {
            if (!highKinds.Contains(kindGrp.Key))
            {
                sb.AppendLine($"### {kindGrp.Key} ({kindGrp.Count()}) — summarized (low concern)");
                sb.AppendLine();
                foreach (var asmGrp in kindGrp.GroupBy(s => s.DeclaringAssembly).OrderBy(g => g.Key))
                {
                    sb.AppendLine($"- {asmGrp.Key}: {asmGrp.Count()}");
                }
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"### {kindGrp.Key} ({kindGrp.Count()})");
            sb.AppendLine();
            foreach (var s in kindGrp.OrderBy(s => s.DeclaringAssembly).ThenBy(s => s.DeclaringType).ThenBy(s => s.Member))
            {
                string key = StaticKey(s);
                string host = _an.StaticHost(key);
                var tasks = ReachingTasks(host);
                string flags = string.Join(",",
                    new[] { s.Written ? "write" : null, s.Read ? "read" : null, s.Initialized ? "init" : null }
                    .Where(x => x is not null));
                sb.AppendLine($"- `{s.DeclaringType}.{s.Member}` : `{s.MemberType}`  [{flags}]  ({s.DeclaringAssembly})");
                sb.AppendLine($"  - reachable from tasks: {FormatTasks(tasks)}");
                sb.AppendLine($"  - example path:\n      -> {PathString(host)}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void BuildSinkSummary(StringBuilder sb, IReadOnlyList<(string hostId, SinkFinding finding)> sinks)
    {
        sb.AppendLine("| Category | Count | Reachable from regular `dotnet restore`? |");
        sb.AppendLine("|----------|------:|:----------------------------------------:|");
        foreach (var grp in sinks.GroupBy(s => s.finding.Category).OrderBy(g => g.Key.ToString()))
        {
            bool regular = grp.Any(x => ReachingTasks(x.hostId).Any(t => IsRegular(t)));
            sb.AppendLine($"| {grp.Key} | {grp.Count()} | {(regular ? "**YES**" : "no (static-graph only)")} |");
        }
        sb.AppendLine();
    }

    private void BuildStaticSummary(StringBuilder sb, IReadOnlyDictionary<string, StaticStateFinding> stat)
    {
        sb.AppendLine("| Kind | Count |");
        sb.AppendLine("|------|------:|");
        foreach (var grp in stat.Values.GroupBy(s => s.Kind).OrderBy(g => g.Key.ToString()))
        {
            sb.AppendLine($"| {grp.Key} | {grp.Count()} |");
        }
        sb.AppendLine();
        sb.AppendLine("By assembly (mutable-ish kinds only):");
        sb.AppendLine();
        sb.AppendLine("| Assembly | Mutable field | Readonly mutable-ref | ThreadStatic | ThreadLocal/AsyncLocal | Lazy | Mutable prop |");
        sb.AppendLine("|----------|---:|---:|---:|---:|---:|---:|");
        foreach (var asmGrp in stat.Values.GroupBy(s => s.DeclaringAssembly).OrderBy(g => g.Key))
        {
            int C(StaticStateKind k) => asmGrp.Count(s => s.Kind == k);
            sb.AppendLine($"| {asmGrp.Key} | {C(StaticStateKind.MutableStaticField)} | {C(StaticStateKind.StaticReadonlyMutableRef)} | {C(StaticStateKind.ThreadStaticField)} | {C(StaticStateKind.ThreadLocalOrAsyncLocal)} | {C(StaticStateKind.LazyStaticField)} | {C(StaticStateKind.MutableStaticProperty)} |");
        }
        sb.AppendLine();
    }

    private static bool IsRegular(string task) => task != "RestoreTaskEx" && task != "GenerateRestoreGraphFileTask";

    private string FormatTasks(List<string> tasks)
    {
        if (tasks.Count == 0) { return "_(none)_"; }
        bool regular = tasks.Any(IsRegular);
        var shown = tasks.Take(8).Select(t => "`" + t + "`");
        string s = string.Join(", ", shown);
        if (tasks.Count > 8) { s += $", +{tasks.Count - 8} more"; }
        return s + (regular ? "  **[regular restore]**" : "  _(static-graph only)_");
    }

    private static string StaticKey(StaticStateFinding s) => s.RawKey;

    public void Probe(string substr)
    {
        int n = 0;
        foreach (var kv in _an.Display)
        {
            if (kv.Value.IndexOf(substr, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
            bool reached = _parent.ContainsKey(kv.Key);
            n++;
            if (n <= 40)
            {
                Console.WriteLine($"  [{(reached ? "REACHED" : "queued ")}] {kv.Value}");
            }
        }
        Console.WriteLine($"  ({n} matching reachable nodes)");
    }

    public void PrintConsoleSummary()
    {
        Console.WriteLine();
        Console.WriteLine("==== SUMMARY ====");
        Console.WriteLine($"Sinks: {_an.Sinks.Count}");
        foreach (var g in _an.Sinks.GroupBy(s => s.finding.Category).OrderBy(g => g.Key.ToString()))
        {
            Console.WriteLine($"  {g.Key,-24} {g.Count()}");
        }
        Console.WriteLine($"Static state members: {_an.StaticState.Count}");
        foreach (var g in _an.StaticState.Values.GroupBy(s => s.Kind).OrderBy(g => g.Key.ToString()))
        {
            Console.WriteLine($"  {g.Key,-28} {g.Count()}");
        }
    }
}
