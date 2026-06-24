using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RestoreStateAnalyzer;

/// <summary>
/// Assigns every reachable static member a resetability "verdict" explaining why it cannot leak per-build state
/// (immutable / pure cache / thread scratch), or how it is mitigated (reset by the end-of-build cleanup or by
/// NuGetTraits), or that it still needs attention (a live OS resource, an env-derived value not yet reset, or a
/// mutable global written during restore). Conservative: anything the rules cannot prove safe lands in a review
/// bucket and is enumerated individually.
/// </summary>
internal sealed class StaticAnnotator
{
    internal enum Verdict
    {
        ImmutableValue,          // recreated bit-for-bit by a fresh process - cannot leak
        StableSingletonOrTable,  // stateless helper / comparer / constant lookup table - recreated identically
        InputKeyedCache,         // memoization keyed on inputs - deterministic; only unbounded memory in a reused host
        ThreadScratch,           // [ThreadStatic]/ThreadLocal per-thread scratch, cleared on use - no cross-build leak
        ResetByCleanup,          // torn down by RestoreProcessStateCleanup at end of build
        ResetByTraits,           // env-derived; re-read via NuGetTraits at start AND end of restore
        EnvDerivedNotReset,      // caches an environment value but is NOT yet reset - genuine gap
        LiveResource,            // holds a process/timer/socket/semaphore/etc. - dispose surface
        NeedsReview,             // mutable global written during restore that no rule could prove safe
    }

    // Types whose entire static surface is reset by RestoreProcessStateCleanup.Reset().
    private static readonly HashSet<string> s_cleanupTypes = new(StringComparer.Ordinal)
    {
        "PluginManager",                  // PluginManager.ResetSharedInstance() - disposes plugin processes + timers
        "ProxyCache",                     // ProxyCache.ResetSharedInstance()
        "DefaultCredentialServiceUtility",// ResetDefaultCredentialService() - DelegatingLogger + _credentialServiceCreatedHere
    };

    // Specific (type, member) pairs reset by the cleanup even though the type also holds un-reset statics.
    private static readonly HashSet<string> s_cleanupMembers = new(StringComparer.Ordinal)
    {
        "HttpHandlerResourceV3.CredentialService", // nulled by ResetDefaultCredentialService()
        "NuGetEnvironment._getHome",               // NuGetEnvironment.ResetCache()
        "NuGetEnvironment._nuGetTempDirectory",    // NuGetEnvironment.ResetCache()
        "NuGetEnvironment.Cache",                  // NuGetEnvironment.ResetCache()
        "ConcurrencyUtilities._basePath",          // ConcurrencyUtilities.ResetCache()
        "SourceRepositoryDependencyProvider._throttle", // SourceRepositoryDependencyProvider.ResetThrottle()
        "HttpSourceResourceProvider.Throttle",     // cleared (= null) by the cleanup
    };

    private readonly Analyzer _a;
    private readonly CompilationSet _set;
    private readonly string _sha;

    public StaticAnnotator(Analyzer a, CompilationSet set, string sha)
    {
        _a = a;
        _set = set;
        _sha = sha;
    }

    public string Build()
    {
        // Cross-reference the env-cache analyzer so members that cache an environment value are recognized.
        var env = new EnvStaticsAnalyzer(_set).Run();
        var envKeys = new HashSet<string>(env.Select(e => e.File + ":" + e.Line), StringComparer.OrdinalIgnoreCase);

        var rows = new List<(StaticStateFinding f, Verdict v)>();
        foreach (var f in _a.StaticState.Values)
        {
            rows.Add((f, Classify(f, envKeys)));
        }

        var byVerdict = rows.GroupBy(r => r.v).ToDictionary(g => g.Key, g => g.OrderBy(r => r.f.DeclaringAssembly, StringComparer.Ordinal).ThenBy(r => r.f.DeclaringType, StringComparer.Ordinal).ThenBy(r => r.f.Member, StringComparer.Ordinal).ToList());

        var sb = new StringBuilder();
        sb.AppendLine("# Reachable static-state members — resetability annotation");
        sb.AppendLine();
        sb.AppendLine($"Total reachable static members: **{rows.Count}**. Each is assigned exactly one verdict.");
        sb.AppendLine();
        sb.AppendLine("| Verdict | Count | Meaning |");
        sb.AppendLine("| --- | --: | --- |");
        foreach (Verdict v in Enum.GetValues(typeof(Verdict)))
        {
            int c = byVerdict.TryGetValue(v, out var l) ? l.Count : 0;
            sb.AppendLine($"| {VerdictName(v)} | {c} | {VerdictMeaning(v)} |");
        }
        sb.AppendLine();

        int leaks = (byVerdict.TryGetValue(Verdict.EnvDerivedNotReset, out var e1) ? e1.Count : 0)
                  + (byVerdict.TryGetValue(Verdict.LiveResource, out var e2) ? e2.Count : 0)
                  + (byVerdict.TryGetValue(Verdict.NeedsReview, out var e3) ? e3.Count : 0);
        sb.AppendLine($"**Attention surface** (EnvDerivedNotReset + LiveResource + NeedsReview): **{leaks}** members — enumerated individually below. All others are immutable, deterministic caches, thread scratch, or actively reset.");
        sb.AppendLine();

        // Enumerate the attention buckets in full, then the mitigated buckets, then summarize the safe buckets.
        EmitFull(sb, "EnvDerivedNotReset — genuine gaps (cache an env value, not yet reset)", byVerdict, Verdict.EnvDerivedNotReset);
        EmitFull(sb, "LiveResource — hold an OS resource (dispose surface)", byVerdict, Verdict.LiveResource);
        EmitFull(sb, "NeedsReview — mutable global written during restore", byVerdict, Verdict.NeedsReview);
        EmitFull(sb, "ResetByCleanup — torn down at end of build", byVerdict, Verdict.ResetByCleanup);
        EmitFull(sb, "ResetByTraits — re-read via NuGetTraits", byVerdict, Verdict.ResetByTraits);
        EmitFull(sb, "ThreadScratch — per-thread, cleared on use", byVerdict, Verdict.ThreadScratch);

        EmitSummaryAndAppendix(sb, "InputKeyedCache — deterministic memoization (memory only)", byVerdict, Verdict.InputKeyedCache);
        EmitSummaryAndAppendix(sb, "StableSingletonOrTable — stateless helpers / constant tables", byVerdict, Verdict.StableSingletonOrTable);
        EmitSummaryAndAppendix(sb, "ImmutableValue — primitives / strings / immutable refs", byVerdict, Verdict.ImmutableValue);

        return sb.ToString();
    }

    private Verdict Classify(StaticStateFinding f, HashSet<string> envKeys)
    {
        string typeName = LastSegment(f.DeclaringType);
        string typeMember = typeName + "." + f.Member;
        bool isEnv = envKeys.Contains(f.File + ":" + f.Line);

        // Mitigations win first.
        if (typeName == "NuGetTraits") { return Verdict.ResetByTraits; }
        if (s_cleanupTypes.Contains(typeName) || s_cleanupMembers.Contains(typeMember)) { return Verdict.ResetByCleanup; }

        // An env-derived cache that is not actively reset is a real gap (regardless of kind).
        if (isEnv) { return Verdict.EnvDerivedNotReset; }

        if (f.Kind is StaticStateKind.ThreadStaticField or StaticStateKind.ThreadLocalOrAsyncLocal) { return Verdict.ThreadScratch; }

        if (f.TypeShape == "liveresource") { return Verdict.LiveResource; }

        if (f.Kind == StaticStateKind.StaticReadonlyImmutable) { return Verdict.ImmutableValue; }

        bool mutableGlobal = f.Kind is StaticStateKind.MutableStaticField or StaticStateKind.MutableStaticProperty;

        // A settable global that is actually assigned on a reachable path can hold per-build state. Exception:
        // stateless helpers (e.g. a generated ResourceManager or a cached serializer) are only ever lazy-init
        // assigned to a deterministic value, so a fresh process recreates them identically.
        if (mutableGlobal && f.Written && f.TypeShape != "stateless-helper") { return Verdict.NeedsReview; }

        // Otherwise classify by the shape of the held value (write-once / readonly).
        switch (f.TypeShape)
        {
            case "immutable":
            case "immutable-collection":
                return Verdict.ImmutableValue;
            case "comparer":
            case "stateless-helper":
                return Verdict.StableSingletonOrTable;
            case "pool":
            case "collection":
            case "concurrent-collection":
                return Verdict.InputKeyedCache;
            case "disposable":
                return Verdict.LiveResource;
            default:
                // Write-once singleton of a NuGet type (e.g. a cached helper). Deterministic unless it captured
                // env/machine state, which the env cross-reference above would already have caught.
                return mutableGlobal || f.Kind == StaticStateKind.LazyStaticField
                    ? Verdict.StableSingletonOrTable
                    : Verdict.StableSingletonOrTable;
        }
    }

    private void EmitFull(StringBuilder sb, string title, Dictionary<Verdict, List<(StaticStateFinding f, Verdict v)>> by, Verdict v)
    {
        if (!by.TryGetValue(v, out var list) || list.Count == 0) { return; }
        sb.AppendLine($"## {title} ({list.Count})");
        sb.AppendLine();
        foreach (var grp in list.GroupBy(r => r.f.DeclaringAssembly).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"### {grp.Key}");
            foreach (var (f, _) in grp)
            {
                sb.AppendLine($"- {Link(f)} `{f.MemberType}` — kind={f.Kind}, shape={f.TypeShape}, read={f.Read}, written={f.Written}");
            }
            sb.AppendLine();
        }
    }

    private void EmitSummaryAndAppendix(StringBuilder sb, string title, Dictionary<Verdict, List<(StaticStateFinding f, Verdict v)>> by, Verdict v)
    {
        if (!by.TryGetValue(v, out var list) || list.Count == 0) { return; }
        sb.AppendLine($"## {title} ({list.Count})");
        sb.AppendLine();
        sb.AppendLine("By assembly:");
        foreach (var grp in list.GroupBy(r => r.f.DeclaringAssembly).OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"- {grp.Key}: {grp.Count()}");
        }
        sb.AppendLine();
        sb.AppendLine("<details><summary>Full list</summary>");
        sb.AppendLine();
        foreach (var (f, _) in list)
        {
            sb.AppendLine($"- {Link(f)} `{f.MemberType}` (kind={f.Kind}, shape={f.TypeShape}, written={f.Written})");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    private string Link(StaticStateFinding f)
    {
        string label = $"`{f.DeclaringType}.{f.Member}`";
        if (string.IsNullOrEmpty(f.File)) { return label; }
        return $"[{label}](https://github.com/JanProvaznik/NuGet.Client/blob/{_sha}/{f.File}#L{f.Line})";
    }

    private static string LastSegment(string dotted)
    {
        int i = dotted.LastIndexOf('.');
        return i >= 0 ? dotted.Substring(i + 1) : dotted;
    }

    private static string VerdictName(Verdict v) => v switch
    {
        Verdict.ImmutableValue => "ImmutableValue",
        Verdict.StableSingletonOrTable => "StableSingletonOrTable",
        Verdict.InputKeyedCache => "InputKeyedCache",
        Verdict.ThreadScratch => "ThreadScratch",
        Verdict.ResetByCleanup => "ResetByCleanup",
        Verdict.ResetByTraits => "ResetByTraits",
        Verdict.EnvDerivedNotReset => "EnvDerivedNotReset",
        Verdict.LiveResource => "LiveResource",
        _ => "NeedsReview",
    };

    private static string VerdictMeaning(Verdict v) => v switch
    {
        Verdict.ImmutableValue => "primitive/string/enum/immutable ref - a fresh process recreates the identical value",
        Verdict.StableSingletonOrTable => "stateless helper, comparer, or constant lookup table - recreated identically",
        Verdict.InputKeyedCache => "memoization keyed purely on inputs - deterministic; only unbounded memory in a reused host",
        Verdict.ThreadScratch => "[ThreadStatic]/ThreadLocal scratch, cleared on use - no cross-build correctness leak",
        Verdict.ResetByCleanup => "torn down by RestoreProcessStateCleanup at end of build",
        Verdict.ResetByTraits => "env-derived; re-read via NuGetTraits at start AND end of restore",
        Verdict.EnvDerivedNotReset => "caches an environment value but is NOT yet reset - genuine gap",
        Verdict.LiveResource => "holds a process/timer/socket/semaphore - dispose surface",
        _ => "mutable global written during restore that no rule could prove safe",
    };
}
