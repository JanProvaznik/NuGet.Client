using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RestoreStateAnalyzer;

/// <summary>
/// Whole-program, cross-assembly interprocedural reachability analysis. Starting from a set of
/// MSBuild task entry points it follows every call, object creation, property/event access and
/// static initializer into the *source* of all referenced NuGet assemblies, recording any static
/// state and process-global ("lingering") side effects it can reach.
/// </summary>
internal sealed class Analyzer
{
    private readonly CompilationSet _set;

    // baseMethodDocId -> overriding/implementing methods (native source symbols)
    private readonly Dictionary<string, List<IMethodSymbol>> _overrideMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _overrideMapSeen = new(StringComparer.Ordinal);

    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Queue<IMethodSymbol> _work = new();
    private readonly HashSet<string> _touchedTypes = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _display = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _edges = new(StringComparer.Ordinal); // caller -> callees

    private readonly List<(string hostId, SinkFinding finding)> _sinks = new();
    private readonly Dictionary<string, StaticStateFinding> _static = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _staticHost = new(StringComparer.Ordinal); // staticKey -> hostId (first)

    // entry method id -> (taskName, group)
    private readonly Dictionary<string, (string task, string group)> _entries = new(StringComparer.Ordinal);

    public Analyzer(CompilationSet set) => _set = set;

    public IReadOnlyList<(string hostId, SinkFinding finding)> Sinks => _sinks;
    public IReadOnlyDictionary<string, StaticStateFinding> StaticState => _static;
    public IReadOnlyDictionary<string, (string task, string group)> Entries => _entries;

    public void DumpOverrides(string substr)
    {
        int shown = 0;
        foreach (var kv in _overrideMap)
        {
            if (kv.Key.IndexOf(substr, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
            Console.WriteLine($"  KEY {kv.Key} -> {kv.Value.Count} impls");
            foreach (var m in kv.Value.Take(20)) { Console.WriteLine($"       {m.ToDisplayString()}"); }
            shown++;
        }
        Console.WriteLine($"  ({shown} matching override-map keys; total keys={_overrideMap.Count})");
    }

    public void BuildOverrideMap()
    {
        foreach (var comp in _set.Projects.Select(p => p.Compilation!).Distinct())
        {
            foreach (var type in GetAllTypes(comp.Assembly.GlobalNamespace))
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.IsOverride && method.OverriddenMethod is { } ov)
                    {
                        AddOverride(ov.OriginalDefinition.GetDocumentationCommentId(), method);
                    }
                }

                if (type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract)
                {
                    foreach (var iface in type.AllInterfaces)
                    {
                        foreach (var im in iface.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (type.FindImplementationForInterfaceMember(im) is IMethodSymbol impl
                                && impl.ContainingAssembly is { } a
                                && _set.ByAssemblyName.ContainsKey(a.Name))
                            {
                                AddOverride(im.OriginalDefinition.GetDocumentationCommentId(), impl.OriginalDefinition);
                            }
                        }
                    }
                }
            }
        }
    }

    private void AddOverride(string? baseKey, IMethodSymbol impl)
    {
        if (baseKey is null) { return; }
        string? implId = impl.OriginalDefinition.GetDocumentationCommentId();
        if (implId is null) { return; }
        if (!_overrideMap.TryGetValue(baseKey, out var list))
        {
            list = new List<IMethodSymbol>();
            _overrideMap[baseKey] = list;
            _overrideMapSeen[baseKey] = new HashSet<string>(StringComparer.Ordinal);
        }
        if (_overrideMapSeen[baseKey].Add(implId)) { list.Add(impl); }
    }

    public void SeedEntryPoints(IEnumerable<(string typeName, string task, string group)> entryTasks)
    {
        var nbt = _set.ByAssemblyName["NuGet.Build.Tasks"];
        foreach (var (typeName, task, group) in entryTasks)
        {
            var type = nbt.GetTypeByMetadataName(typeName);
            if (type is null)
            {
                Console.WriteLine($"  WARNING: entry type not found: {typeName}");
                continue;
            }

            foreach (var t in EnumerateWithBases(type))
            {
                foreach (var member in t.GetMembers())
                {
                    if (member is IMethodSymbol m)
                    {
                        if ((m.MethodKind == MethodKind.Constructor && SymbolEqualityComparer.Default.Equals(t, type))
                            || m.Name is "Execute" or "Cancel" or "Dispose")
                        {
                            SeedMethod(m, task, group);
                        }
                    }
                    else if (member is IPropertySymbol p)
                    {
                        if (p.GetMethod is { } g) { SeedMethod(g, task, group); }
                        if (p.SetMethod is { } s) { SeedMethod(s, task, group); }
                    }
                }
            }
        }
    }

    private void SeedMethod(IMethodSymbol m, string task, string group)
    {
        var od = m.OriginalDefinition;
        string? id = od.GetDocumentationCommentId();
        if (id is null) { return; }
        _entries[id] = (task, group);
        _display[id] = od.ToDisplayString();
        Enqueue(od, null, "(entrypoint)");
    }

    public void Run()
    {
        while (_work.Count > 0)
        {
            ProcessMethod(_work.Dequeue());
        }
    }

    private void ProcessMethod(IMethodSymbol method)
    {
        var od = method.OriginalDefinition;
        string id = MethodId(od);
        if (!_visited.Add(id)) { return; }

        var home = ResolveHomeMethod(od);
        if (home is null) { return; } // external / no source body

        // Touch the declaring type: using any member of a type triggers its static initialization.
        if (home.ContainingType is { } ct) { TouchType(ct, id); }

        foreach (var sref in home.DeclaringSyntaxReferences)
        {
            var node = sref.GetSyntax();
            if (!_set.TreeOwner.TryGetValue(node.SyntaxTree, out var comp)) { continue; }
            var sm = comp.GetSemanticModel(node.SyntaxTree);
            IOperation? op;
            try { op = sm.GetOperation(node); }
            catch { continue; }
            if (op is null) { continue; }
            WalkBody(op, id);
        }
    }

    private void WalkBody(IOperation root, string methodId)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var op = stack.Pop();
            switch (op)
            {
                case IInvocationOperation inv:
                    HandleInvocation(inv, methodId);
                    break;
                case IObjectCreationOperation oc:
                    HandleCreation(oc, methodId);
                    break;
                case IFieldReferenceOperation fr:
                    HandleFieldRef(fr, methodId);
                    break;
                case IPropertyReferenceOperation pr:
                    HandlePropertyRef(pr, methodId);
                    break;
                case IEventReferenceOperation er:
                    HandleEvent(er.Event, er, methodId);
                    break;
                case IEventAssignmentOperation ea when ea.EventReference is IEventReferenceOperation er2:
                    HandleEvent(er2.Event, ea, methodId);
                    break;
                case IMethodReferenceOperation mr:
                    EnqueueCall(mr.Method, methodId, mr); // method group -> delegate (may be invoked later)
                    break;
            }

            foreach (var child in op.ChildOperations)
            {
                stack.Push(child);
            }
        }
    }

    private void HandleInvocation(IInvocationOperation inv, string methodId)
    {
        var target = inv.TargetMethod;
        if (SinkRules.TryClassifyInvocation(target, out var cat, out var api))
        {
            RecordSink(cat, api, methodId, inv);
        }
        EnqueueCall(target, methodId, inv);
    }

    private void HandleCreation(IObjectCreationOperation oc, string methodId)
    {
        if (oc.Type is INamedTypeSymbol nt)
        {
            if (SinkRules.TryClassifyCreation(nt, out var cat, out var api))
            {
                RecordSink(cat, api, methodId, oc);
            }
            TouchType(nt, methodId);
        }
        if (oc.Constructor is { } ctor)
        {
            EnqueueCall(ctor, methodId, oc);
        }
    }

    private void HandleFieldRef(IFieldReferenceOperation fr, string methodId)
    {
        var f = fr.Field;
        if (!f.IsStatic || f.IsConst) { return; }
        bool write = IsWriteContext(fr);

        if (f.IsImplicitlyDeclared)
        {
            if (f.AssociatedSymbol is IPropertySymbol aps) { RecordStaticProperty(aps, methodId, !write, write); }
            return;
        }

        TouchType(f.ContainingType, methodId);
        RecordStaticField(f, methodId, read: !write, written: write, initialized: false);
    }

    private void HandlePropertyRef(IPropertyReferenceOperation pr, string methodId)
    {
        var p = pr.Property;
        bool write = IsWriteContext(pr);

        if (write && SinkRules.TryClassifyPropertyWrite(p, out var cat, out var api))
        {
            RecordSink(cat, api, methodId, pr);
        }

        if (p.IsStatic)
        {
            TouchType(p.ContainingType, methodId);
            RecordStaticProperty(p, methodId, !write, write);
        }

        if (write && p.SetMethod is { } sm) { EnqueueCall(sm, methodId, pr); }
        if (!write && p.GetMethod is { } gm) { EnqueueCall(gm, methodId, pr); }
        if (write && IsCompound(pr) && p.GetMethod is { } gm2) { EnqueueCall(gm2, methodId, pr); }
    }

    private void HandleEvent(IEventSymbol e, IOperation op, string methodId)
    {
        if (SinkRules.TryClassifyEvent(e, out var cat, out var api))
        {
            RecordSink(cat, api, methodId, op);
        }
        if (e.AddMethod is { } am) { EnqueueCall(am, methodId, op); }
        if (e.RemoveMethod is { } rm) { EnqueueCall(rm, methodId, op); }
    }

    private void EnqueueCall(IMethodSymbol target, string callerId, IOperation site)
    {
        var m = target;
        if (m.ReducedFrom is { } rf) { m = rf; }
        var od = m.OriginalDefinition;

        // sinks already checked at call site; only traverse source-defined methods.
        if (od.ContainingAssembly is not { } asm || !_set.ByAssemblyName.ContainsKey(asm.Name))
        {
            return;
        }

        Enqueue(od, callerId, "");

        // Virtual / interface dispatch: enqueue all known source overrides/implementations,
        // transitively (interface member -> abstract base impl -> concrete overrides).
        if (IsDispatchable(od) && od.GetDocumentationCommentId() is { } key)
        {
            foreach (var impl in DispatchClosure(key))
            {
                Enqueue(impl, callerId, "");
            }
        }
    }

    private readonly Dictionary<string, List<IMethodSymbol>> _closureCache = new(StringComparer.Ordinal);

    private List<IMethodSymbol> DispatchClosure(string key)
    {
        if (_closureCache.TryGetValue(key, out var cached)) { return cached; }
        var result = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        var stack = new Stack<string>();
        stack.Push(key);
        while (stack.Count > 0)
        {
            var k = stack.Pop();
            if (!_overrideMap.TryGetValue(k, out var impls)) { continue; }
            foreach (var impl in impls)
            {
                string? ik = impl.OriginalDefinition.GetDocumentationCommentId();
                if (ik is null) { result.Add(impl); continue; }
                if (seen.Add(ik)) { result.Add(impl); stack.Push(ik); }
            }
        }
        _closureCache[key] = result;
        return result;
    }

    private void Enqueue(IMethodSymbol od, string? callerId, string _)
    {
        string id = MethodId(od);
        _display.TryAdd(id, od.ToDisplayString());
        if (callerId is not null) { AddEdge(callerId, id); }
        if (_visited.Contains(id) || !_queued.Add(id)) { return; }
        _work.Enqueue(od);
    }

    private void TouchType(INamedTypeSymbol type, string callerId)
    {
        var t = type.OriginalDefinition;
        if (t.ContainingAssembly is not { } asm || !_set.ByAssemblyName.ContainsKey(asm.Name)) { return; }
        string tid = "T:" + (t.GetDocumentationCommentId() ?? t.ToDisplayString());
        AddEdge(callerId, tid);
        if (!_touchedTypes.Add(tid)) { return; }
        _display[tid] = "<static init> " + t.ToDisplayString();

        var home = ResolveHomeType(t);
        if (home is null) { return; }

        foreach (var sc in home.StaticConstructors)
        {
            Enqueue(sc.OriginalDefinition, tid, "");
        }

        foreach (var dref in home.DeclaringSyntaxReferences)
        {
            if (dref.GetSyntax() is not TypeDeclarationSyntax tds) { continue; }
            if (!_set.TreeOwner.TryGetValue(tds.SyntaxTree, out var comp)) { continue; }
            var sm = comp.GetSemanticModel(tds.SyntaxTree);

            foreach (var member in tds.Members)
            {
                if (member is FieldDeclarationSyntax fds && fds.Modifiers.Any(SyntaxKind.StaticKeyword))
                {
                    foreach (var v in fds.Declaration.Variables)
                    {
                        if (v.Initializer is null) { continue; }
                        if (sm.GetDeclaredSymbol(v) is IFieldSymbol fsym && !fsym.IsConst)
                        {
                            RecordStaticField(fsym, tid, read: false, written: false, initialized: true);
                        }
                        if (sm.GetOperation(v.Initializer.Value) is { } iop) { WalkBody(iop, tid); }
                    }
                }
                else if (member is PropertyDeclarationSyntax pds && pds.Modifiers.Any(SyntaxKind.StaticKeyword) && pds.Initializer is not null)
                {
                    if (sm.GetDeclaredSymbol(pds) is IPropertySymbol psym)
                    {
                        RecordStaticProperty(psym, tid, read: false, written: false, initialized: true);
                    }
                    if (sm.GetOperation(pds.Initializer.Value) is { } iop) { WalkBody(iop, tid); }
                }
            }
        }
    }

    // ---- recording ----

    private void RecordSink(SinkCategory cat, string api, string methodId, IOperation op)
    {
        var span = op.Syntax.GetLocation().GetLineSpan();
        string location = $"{System.IO.Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
        string asm = op.SemanticModel?.Compilation.AssemblyName ?? "";
        string key = $"{cat}|{api}|{location}";
        if (_sinks.Any(s => s.finding.Category == cat && s.finding.Api == api && s.finding.Location == location)) { return; }
        _sinks.Add((methodId, new SinkFinding
        {
            Category = cat,
            Api = api,
            EnclosingMethod = _display.TryGetValue(methodId, out var d) ? d : methodId,
            Location = location,
            EnclosingAssembly = asm,
        }));
    }

    private void RecordStaticField(IFieldSymbol f, string hostId, bool read, bool written, bool initialized)
    {
        bool isSource = f.ContainingAssembly is { } a && _set.ByAssemblyName.ContainsKey(a.Name);
        if (!isSource && !written) { return; } // external reads = noise

        string key = f.OriginalDefinition.GetDocumentationCommentId() ?? f.ToDisplayString();
        if (!_static.TryGetValue(key, out var sf))
        {
            sf = new StaticStateFinding
            {
                RawKey = key,
                DeclaringAssembly = f.ContainingAssembly?.Name ?? "",
                DeclaringType = SinkRules.TypeName(f.ContainingType),
                Member = f.Name,
                MemberType = f.Type.ToDisplayString(),
                Kind = ClassifyField(f),
            };
            _static[key] = sf;
            _staticHost[key] = hostId;
        }
        sf.Read |= read; sf.Written |= written; sf.Initialized |= initialized;
    }

    private void RecordStaticProperty(IPropertySymbol p, string hostId, bool read, bool written, bool initialized = false)
    {
        bool isSource = p.ContainingAssembly is { } a && _set.ByAssemblyName.ContainsKey(a.Name);
        if (!isSource) { return; }
        if (!p.IsStatic) { return; }

        string key = p.OriginalDefinition.GetDocumentationCommentId() ?? p.ToDisplayString();
        if (!_static.TryGetValue(key, out var sf))
        {
            sf = new StaticStateFinding
            {
                RawKey = key,
                DeclaringAssembly = p.ContainingAssembly?.Name ?? "",
                DeclaringType = SinkRules.TypeName(p.ContainingType),
                Member = p.Name,
                MemberType = p.Type.ToDisplayString(),
                Kind = p.SetMethod is not null || IsMutableRef(p.Type) ? StaticStateKind.MutableStaticProperty : StaticStateKind.StaticReadonlyImmutable,
            };
            _static[key] = sf;
            _staticHost[key] = hostId;
        }
        sf.Read |= read; sf.Written |= written; sf.Initialized |= initialized;
    }

    private static StaticStateKind ClassifyField(IFieldSymbol f)
    {
        if (f.GetAttributes().Any(a => a.AttributeClass?.Name == "ThreadStaticAttribute"))
        {
            return StaticStateKind.ThreadStaticField;
        }
        string typeName = f.Type.OriginalDefinition.ToDisplayString();
        if (typeName is "System.Threading.ThreadLocal<T>" or "System.Threading.AsyncLocal<T>")
        {
            return StaticStateKind.ThreadLocalOrAsyncLocal;
        }
        if (typeName == "System.Lazy<T>")
        {
            return StaticStateKind.LazyStaticField;
        }
        if (!f.IsReadOnly)
        {
            return StaticStateKind.MutableStaticField;
        }
        return IsMutableRef(f.Type) ? StaticStateKind.StaticReadonlyMutableRef : StaticStateKind.StaticReadonlyImmutable;
    }

    private static bool IsMutableRef(ITypeSymbol t)
    {
        if (t.SpecialType is SpecialType.System_String or SpecialType.System_Boolean
            or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double
            or SpecialType.System_Char or SpecialType.System_Byte or SpecialType.System_Int16)
        {
            return false;
        }
        if (t.TypeKind == TypeKind.Enum) { return false; }
        string n = t.OriginalDefinition.ToDisplayString();
        return n switch
        {
            "System.Guid" or "System.TimeSpan" or "System.DateTime" or "System.DateTimeOffset"
            or "System.Version" or "System.Uri" or "System.Decimal" or "System.Type" => false,
            _ => true,
        };
    }

    // ---- helpers ----

    private void AddEdge(string from, string to)
    {
        if (!_edges.TryGetValue(from, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); _edges[from] = set; }
        set.Add(to);
    }

    public IReadOnlyDictionary<string, HashSet<string>> Edges => _edges;
    public IReadOnlyDictionary<string, string> Display => _display;
    public string StaticHost(string key) => _staticHost.TryGetValue(key, out var h) ? h : "";

    private static string MethodId(IMethodSymbol od) => od.GetDocumentationCommentId() ?? od.ToDisplayString();

    private static bool IsDispatchable(IMethodSymbol m)
        => m.IsVirtual || m.IsAbstract || m.IsOverride || m.ContainingType?.TypeKind == TypeKind.Interface;

    private static bool IsWriteContext(IOperation operation)
    {
        var parent = operation.Parent;
        return parent switch
        {
            ISimpleAssignmentOperation sa => sa.Target == operation,
            ICompoundAssignmentOperation ca => ca.Target == operation,
            ICoalesceAssignmentOperation co => co.Target == operation,
            IIncrementOrDecrementOperation => true,
            IArgumentOperation arg => arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out,
            _ => false,
        };
    }

    private static bool IsCompound(IOperation operation)
        => operation.Parent is ICompoundAssignmentOperation or ICoalesceAssignmentOperation;

    private IMethodSymbol? ResolveHomeMethod(IMethodSymbol od)
    {
        if (od.ContainingAssembly is not { } asm || !_set.ByAssemblyName.TryGetValue(asm.Name, out var homeComp))
        {
            return null;
        }
        if (od.DeclaringSyntaxReferences.Any(r => _set.TreeOwner.ContainsKey(r.SyntaxTree)))
        {
            return od;
        }
        if (od.GetDocumentationCommentId() is { } docId
            && DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, homeComp) is IMethodSymbol sym
            && sym.DeclaringSyntaxReferences.Any(r => _set.TreeOwner.ContainsKey(r.SyntaxTree)))
        {
            return sym;
        }
        return null;
    }

    private INamedTypeSymbol? ResolveHomeType(INamedTypeSymbol t)
    {
        if (t.ContainingAssembly is not { } asm || !_set.ByAssemblyName.TryGetValue(asm.Name, out var homeComp))
        {
            return null;
        }
        if (t.DeclaringSyntaxReferences.Any(r => _set.TreeOwner.ContainsKey(r.SyntaxTree)))
        {
            return t;
        }
        if (t.GetDocumentationCommentId() is { } docId
            && DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, homeComp) is INamedTypeSymbol sym)
        {
            return sym;
        }
        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateWithBases(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            yield return t;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
        {
            foreach (var nested in WithNested(t)) { yield return nested; }
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

