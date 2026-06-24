using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RestoreStateAnalyzer;

/// <summary>
/// Finds process-wide CACHED environment-variable reads: static fields/properties whose value is derived
/// (directly, via a Lazy factory, or via a "??=" cache assignment) from an environment variable and therefore
/// fixed for the process lifetime. This is more accurate than grep: it follows calls cross-assembly into
/// helpers (e.g. NuGetEnvironment.GetHome) and ignores per-call reads that correctly use IEnvironmentVariableReader.
/// </summary>
internal sealed class EnvStaticsAnalyzer
{
    private readonly CompilationSet _set;

    private readonly Dictionary<string, HashSet<string>> _edges = new(StringComparer.Ordinal);       // caller -> callees
    private readonly Dictionary<string, HashSet<string>> _reverse = new(StringComparer.Ordinal);      // callee -> callers
    private readonly HashSet<string> _directEnv = new(StringComparer.Ordinal);                        // methods that read env directly
    private readonly Dictionary<string, HashSet<string>> _directEnvNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reachesEnv = new(StringComparer.Ordinal);

    // static field/prop key -> list of assignment RHS (each: invoked method keys + whether direct env)
    private readonly Dictionary<string, List<(HashSet<string> calls, bool directEnv, HashSet<string> names)>> _writes = new(StringComparer.Ordinal);

    public EnvStaticsAnalyzer(CompilationSet set) => _set = set;

    public sealed record Finding(string Assembly, string Member, string MemberType, string File, int Line, string How, string Vars);

    public List<Finding> Run()
    {
        BuildGraph();
        ComputeReachesEnv();
        return ReportStatics();
    }

    // ---- Phase 1: call graph + direct env reads + static-field write RHS ----

    private void BuildGraph()
    {
        foreach (var comp in _set.Projects.Select(p => p.Compilation!).Distinct())
        {
            foreach (var type in GetAllTypes(comp.Assembly.GlobalNamespace))
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    var od = method.OriginalDefinition;
                    var home = ResolveHome(od);
                    if (home is null) { continue; }
                    string callerKey = MethodId(od);

                    foreach (var sref in home.DeclaringSyntaxReferences)
                    {
                        var node = sref.GetSyntax();
                        if (!_set.TreeOwner.TryGetValue(node.SyntaxTree, out var c)) { continue; }
                        var op = TryGetOperation(c, node);
                        if (op is null) { continue; }
                        WalkForGraph(op, callerKey);
                    }
                }
            }
        }
    }

    private void WalkForGraph(IOperation root, string callerKey)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var op = stack.Pop();

            switch (op)
            {
                case IInvocationOperation inv:
                    RecordCall(callerKey, inv.TargetMethod, inv);
                    break;
                case IObjectCreationOperation oc when oc.Constructor is { } ctor:
                    AddEdge(callerKey, MethodId(ctor.OriginalDefinition));
                    break;
                case IPropertyReferenceOperation pr when !IsWrite(pr) && pr.Property.GetMethod is { } gm:
                    AddEdge(callerKey, MethodId(gm.OriginalDefinition));
                    break;
                case ISimpleAssignmentOperation sa when sa.Target is IFieldReferenceOperation fr && fr.Field.IsStatic:
                    RecordStaticWrite(MemberId(fr.Field.OriginalDefinition), sa.Value);
                    break;
                case ISimpleAssignmentOperation sap when sap.Target is IPropertyReferenceOperation pw && pw.Property.IsStatic:
                    RecordStaticWrite(MemberId(pw.Property.OriginalDefinition), sap.Value);
                    break;
                case ICoalesceAssignmentOperation ca when ca.Target is IFieldReferenceOperation fr2 && fr2.Field.IsStatic:
                    RecordStaticWrite(MemberId(fr2.Field.OriginalDefinition), ca.Value);
                    break;
                case ICoalesceAssignmentOperation cap when cap.Target is IPropertyReferenceOperation pw2 && pw2.Property.IsStatic:
                    RecordStaticWrite(MemberId(pw2.Property.OriginalDefinition), cap.Value);
                    break;
            }

            foreach (var child in op.ChildOperations) { stack.Push(child); }
        }
    }

    private void RecordCall(string callerKey, IMethodSymbol target, IInvocationOperation inv)
    {
        var m = target.ReducedFrom ?? target;
        var od = m.OriginalDefinition;
        if (IsEnvRead(od, out string? var))
        {
            _directEnv.Add(callerKey);
            if (var is not null) { Names(_directEnvNames, callerKey).Add(var); }
            else if (TryConstArg(inv) is { } cv) { Names(_directEnvNames, callerKey).Add(cv); }
            return;
        }
        if (od.ContainingAssembly is { } a && _set.ByAssemblyName.ContainsKey(a.Name))
        {
            AddEdge(callerKey, MethodId(od));
        }
    }

    private void RecordStaticWrite(string key, IOperation rhs)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool direct = false;
        var stack = new Stack<IOperation>();
        stack.Push(rhs);
        while (stack.Count > 0)
        {
            var op = stack.Pop();
            if (op is IInvocationOperation inv)
            {
                var od = (inv.TargetMethod.ReducedFrom ?? inv.TargetMethod).OriginalDefinition;
                if (IsEnvRead(od, out string? v)) { direct = true; if (v is not null) names.Add(v); else if (TryConstArg(inv) is { } cv) names.Add(cv); }
                else { calls.Add(MethodId(od)); }
            }
            foreach (var ch in op.ChildOperations) { stack.Push(ch); }
        }
        if (!_writes.TryGetValue(key, out var list)) { list = new(); _writes[key] = list; }
        list.Add((calls, direct, names));
    }

    // ---- Phase 2: reverse reachability ----

    private void ComputeReachesEnv()
    {
        foreach (var kv in _edges)
        {
            foreach (var callee in kv.Value)
            {
                if (!_reverse.TryGetValue(callee, out var set)) { set = new(StringComparer.Ordinal); _reverse[callee] = set; }
                set.Add(kv.Key);
            }
        }
        var q = new Queue<string>();
        foreach (var k in _directEnv) { if (_reachesEnv.Add(k)) { q.Enqueue(k); } }
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (!_reverse.TryGetValue(cur, out var callers)) { continue; }
            foreach (var caller in callers) { if (_reachesEnv.Add(caller)) { q.Enqueue(caller); } }
        }
    }

    // ---- Phase 3: report cached static env members ----

    private List<Finding> ReportStatics()
    {
        var findings = new List<Finding>();
        foreach (var comp in _set.Projects.Select(p => p.Compilation!).Distinct())
        {
            foreach (var type in GetAllTypes(comp.Assembly.GlobalNamespace))
            {
                foreach (var f in type.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!f.IsStatic || f.IsConst) { continue; }
                    EvaluateMember(f, f.Type.ToDisplayString(), findings);
                }
                foreach (var p in type.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!p.IsStatic) { continue; }
                    EvaluateMember(p, p.Type.ToDisplayString(), findings);
                }
            }
        }
        return findings
            .GroupBy(f => f.Assembly + "|" + f.Member)
            .Select(g => g.First())
            .OrderBy(f => f.Assembly, StringComparer.Ordinal).ThenBy(f => f.Member, StringComparer.Ordinal)
            .ToList();
    }

    private void EvaluateMember(ISymbol member, string memberType, List<Finding> findings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var hows = new HashSet<string>(StringComparer.Ordinal);
        bool cached = false;

        // (a) initializer (field declarator / property initializer), incl. Lazy factory lambdas
        foreach (var sref in member.DeclaringSyntaxReferences)
        {
            var node = sref.GetSyntax();
            ExpressionSyntax? init = node switch
            {
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                PropertyDeclarationSyntax pd => pd.Initializer?.Value,
                _ => null,
            };
            if (init is null) { continue; }
            if (!_set.TreeOwner.TryGetValue(init.SyntaxTree, out var c)) { continue; }
            var op = TryGetOperation(c, init);
            if (op is null) { continue; }
            if (EvaluateExpression(op, names, hows)) { cached = true; }
        }

        // (b) "??=" / assignment caches (e.g. _nuGetTempDirectory ??= GetNuGetTempDirectory())
        string key = MemberId(member.OriginalDefinition);
        if (_writes.TryGetValue(key, out var writes))
        {
            foreach (var (calls, direct, wn) in writes)
            {
                if (direct) { cached = true; hows.Add("assigned-from-env"); foreach (var n in wn) names.Add(n); }
                foreach (var call in calls)
                {
                    if (_reachesEnv.Contains(call)) { cached = true; hows.Add("assigned via " + ShortName(call)); }
                }
            }
        }

        if (!cached) { return; }

        var loc = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().GetLocation().GetLineSpan();
        if (loc is null) { return; }
        findings.Add(new Finding(
            member.ContainingAssembly?.Name ?? "",
            SinkRules.TypeName(member.ContainingType) + "." + member.Name,
            memberType,
            RepoRelative(loc.Value.Path),
            loc.Value.StartLinePosition.Line + 1,
            string.Join("; ", hows.OrderBy(x => x)),
            string.Join(", ", names.OrderBy(x => x))));
    }

    private bool EvaluateExpression(IOperation root, HashSet<string> names, HashSet<string> hows)
    {
        bool cached = false;
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var op = stack.Pop();
            if (op is IInvocationOperation inv)
            {
                var od = (inv.TargetMethod.ReducedFrom ?? inv.TargetMethod).OriginalDefinition;
                if (IsEnvRead(od, out string? v)) { cached = true; hows.Add("direct env read"); if (v is not null) names.Add(v); else if (TryConstArg(inv) is { } cv) names.Add(cv); }
                else if (_reachesEnv.Contains(MethodId(od))) { cached = true; hows.Add("via " + od.Name); }
            }
            else if (op is IObjectCreationOperation oc && oc.Constructor is { } ctor && _reachesEnv.Contains(MethodId(ctor.OriginalDefinition)))
            {
                cached = true; hows.Add("via ctor " + oc.Type?.Name);
            }
            else if (op is IPropertyReferenceOperation pr && pr.Property.GetMethod is { } gm && _reachesEnv.Contains(MethodId(gm.OriginalDefinition)))
            {
                cached = true; hows.Add("via " + pr.Property.Name);
            }
            foreach (var ch in op.ChildOperations) { stack.Push(ch); }
        }
        return cached;
    }

    // ---- helpers ----

    private static bool IsEnvRead(IMethodSymbol m, out string? var)
    {
        var = null;
        string t = SinkRules.TypeName(m.ContainingType);
        string n = m.Name;
        if (t == "System.Environment" && n is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "ExpandEnvironmentVariables" or "GetFolderPath")
        {
            return true;
        }
        if (n == "GetEnvironmentVariable" && (t == "NuGet.Common.IEnvironmentVariableReader" || t == "NuGet.Common.EnvironmentVariableWrapper"))
        {
            return true;
        }
        return false;
    }

    private static string? TryConstArg(IInvocationOperation inv)
    {
        if (inv.Arguments.Length == 0) { return null; }
        var c = inv.Arguments[0].Value.ConstantValue;
        return c.HasValue ? c.Value as string : null;
    }

    private void AddEdge(string from, string to)
    {
        if (!_edges.TryGetValue(from, out var set)) { set = new(StringComparer.Ordinal); _edges[from] = set; }
        set.Add(to);
    }

    private static HashSet<string> Names(Dictionary<string, HashSet<string>> d, string k)
    {
        if (!d.TryGetValue(k, out var s)) { s = new(StringComparer.Ordinal); d[k] = s; }
        return s;
    }

    private IMethodSymbol? ResolveHome(IMethodSymbol od)
    {
        if (od.ContainingAssembly is not { } asm || !_set.ByAssemblyName.TryGetValue(asm.Name, out var homeComp)) { return null; }
        if (od.DeclaringSyntaxReferences.Any(r => _set.TreeOwner.ContainsKey(r.SyntaxTree))) { return od; }
        if (od.GetDocumentationCommentId() is { } id
            && DocumentationCommentId.GetFirstSymbolForDeclarationId(id, homeComp) is IMethodSymbol sym
            && sym.DeclaringSyntaxReferences.Any(r => _set.TreeOwner.ContainsKey(r.SyntaxTree)))
        {
            return sym;
        }
        return null;
    }

    private static IOperation? TryGetOperation(Compilation comp, SyntaxNode node)
    {
        try { return comp.GetSemanticModel(node.SyntaxTree).GetOperation(node); }
        catch { return null; }
    }

    private static bool IsWrite(IOperation op)
        => op.Parent is ISimpleAssignmentOperation sa && sa.Target == op
        || op.Parent is ICompoundAssignmentOperation ca && ca.Target == op
        || op.Parent is ICoalesceAssignmentOperation co && co.Target == op;

    private static string MethodId(IMethodSymbol od) => od.GetDocumentationCommentId() ?? od.ToDisplayString();
    private static string MemberId(ISymbol od) => od.GetDocumentationCommentId() ?? od.ToDisplayString();
    private static string ShortName(string docId) { int i = docId.LastIndexOf('.'); return i > 0 ? docId.Substring(i + 1) : docId; }

    private static string RepoRelative(string path)
    {
        const string root = @"Q:\nuget.client\";
        string p = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        return p.Replace('\\', '/');
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
        {
            foreach (var x in WithNested(t)) { yield return x; }
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var t in GetAllTypes(child)) { yield return t; }
        }
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol t)
    {
        yield return t;
        foreach (var n in t.GetTypeMembers())
        {
            foreach (var x in WithNested(n)) { yield return x; }
        }
    }
}
