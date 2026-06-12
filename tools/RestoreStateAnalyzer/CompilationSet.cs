using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RestoreStateAnalyzer;

/// <summary>
/// Reconstructs an accurate, cross-assembly set of Roslyn compilations (one per NuGet source
/// project) from the exact csc command lines captured in an MSBuild binary log. References between
/// NuGet source projects are wired as <see cref="CompilationReference"/> so that interprocedural
/// analysis can follow calls into the *source* of other assemblies (true cross-assembly accuracy).
/// </summary>
internal sealed class CompilationSet
{
    public sealed class ProjInfo
    {
        public required string ProjectPath { get; init; }
        public required string ProjectDir { get; init; }
        public required string AssemblyName { get; init; }
        public required CSharpCommandLineArguments Args { get; init; }
        public required List<SyntaxTree> Trees { get; init; }
        public required List<string> SourceProjectRefs { get; init; } // assembly names
        public Compilation? Compilation { get; set; }
    }

    public List<ProjInfo> Projects { get; } = new();
    public Dictionary<string, Compilation> ByAssemblyName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<SyntaxTree, Compilation> TreeOwner { get; } = new();
    public HashSet<string> SourceAssemblyNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string s_sdkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

    public static CompilationSet BuildFromBinlog(string binlogPath)
    {
        var set = new CompilationSet();
        var invocations = CompilerInvocationsReader.ReadInvocations(binlogPath)
            .Where(i => string.Equals(i.Language, CompilerInvocation.CSharp, StringComparison.OrdinalIgnoreCase)
                        || i.Language == "C#")
            .ToList();

        // First pass: parse args, source trees, identify assembly names.
        foreach (var inv in invocations)
        {
            var tokens = Tokenize(inv.CommandLineArguments!);
            CSharpCommandLineArguments parsed = CSharpCommandLineParser.Default.Parse(tokens, inv.ProjectDirectory, s_sdkDir);
            string asmName = parsed.CompilationName ?? Path.GetFileNameWithoutExtension(inv.ProjectFilePath);

            var trees = new List<SyntaxTree>(parsed.SourceFiles.Length);
            foreach (var sf in parsed.SourceFiles)
            {
                string path = sf.Path;
                if (!File.Exists(path)) { continue; }
                string text = File.ReadAllText(path);
                trees.Add(CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(text, Encoding.UTF8), parsed.ParseOptions, path));
            }

            set.Projects.Add(new ProjInfo
            {
                ProjectPath = inv.ProjectFilePath,
                ProjectDir = inv.ProjectDirectory,
                AssemblyName = asmName,
                Args = parsed,
                Trees = trees,
                SourceProjectRefs = new List<string>(),
            });
            set.SourceAssemblyNames.Add(asmName);
        }

        // Second pass: classify each metadata reference as source-project vs external.
        foreach (var p in set.Projects)
        {
            foreach (var cr in p.Args.MetadataReferences)
            {
                string refPath = cr.Reference;
                string nameNoExt = Path.GetFileNameWithoutExtension(refPath);
                if (set.SourceAssemblyNames.Contains(nameNoExt) && !string.Equals(nameNoExt, p.AssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    p.SourceProjectRefs.Add(nameNoExt);
                }
            }
        }

        // Topological order over source-project references.
        var ordered = TopoSort(set.Projects);

        // Build compilations in order, substituting CompilationReference for already-built source deps.
        foreach (var p in ordered)
        {
            var references = new List<MetadataReference>();
            foreach (var cr in p.Args.MetadataReferences)
            {
                string refPath = cr.Reference;
                if (!Path.IsPathRooted(refPath))
                {
                    refPath = Path.GetFullPath(Path.Combine(p.ProjectDir, refPath));
                }
                string nameNoExt = Path.GetFileNameWithoutExtension(refPath);
                var props = new MetadataReferenceProperties(MetadataImageKind.Assembly, cr.Properties.Aliases, cr.Properties.EmbedInteropTypes);

                if (set.SourceAssemblyNames.Contains(nameNoExt)
                    && !string.Equals(nameNoExt, p.AssemblyName, StringComparison.OrdinalIgnoreCase)
                    && set.ByAssemblyName.TryGetValue(nameNoExt, out var depComp))
                {
                    references.Add(depComp.ToMetadataReference(props.Aliases, props.EmbedInteropTypes));
                }
                else if (File.Exists(refPath))
                {
                    references.Add(MetadataReference.CreateFromFile(refPath, props));
                }
            }

            var options = ((CSharpCompilationOptions)p.Args.CompilationOptions)
                .WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                .WithCryptoKeyFile(null)
                .WithStrongNameProvider(null)
                .WithDelaySign(false)
                .WithPublicSign(false)
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>())
                .WithGeneralDiagnosticOption(ReportDiagnostic.Suppress);

            var compilation = CSharpCompilation.Create(p.AssemblyName, p.Trees, references, options);

            // Run source generators (e.g. System.Text.Json) so generated partial types resolve.
            compilation = RunGenerators(compilation, p);

            p.Compilation = compilation;
            p.Trees.Clear();
            p.Trees.AddRange(compilation.SyntaxTrees);
            set.ByAssemblyName[p.AssemblyName] = compilation;
            foreach (var t in compilation.SyntaxTrees)
            {
                set.TreeOwner[t] = compilation;
            }
        }

        return set;
    }

    private static readonly Dictionary<string, ImmutableArray<ISourceGenerator>> s_generatorCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SimpleAnalyzerLoader s_loader = new();

    private static CSharpCompilation RunGenerators(CSharpCompilation compilation, ProjInfo p)
    {
        var generators = new List<ISourceGenerator>();
        foreach (var ar in p.Args.AnalyzerReferences)
        {
            string path = ar.FilePath;
            if (!Path.IsPathRooted(path)) { path = Path.GetFullPath(Path.Combine(p.ProjectDir, path)); }
            if (!File.Exists(path)) { continue; }

            if (!s_generatorCache.TryGetValue(path, out var gens))
            {
                try
                {
                    var aref = new AnalyzerFileReference(path, s_loader);
                    gens = aref.GetGenerators(LanguageNames.CSharp);
                }
                catch
                {
                    gens = ImmutableArray<ISourceGenerator>.Empty;
                }
                s_generatorCache[path] = gens;
            }
            generators.AddRange(gens);
        }

        if (generators.Count == 0) { return compilation; }

        try
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: (CSharpParseOptions)p.Args.ParseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
            return (CSharpCompilation)updated;
        }
        catch
        {
            return compilation;
        }
    }

    private sealed class SimpleAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private static List<ProjInfo> TopoSort(List<ProjInfo> projects)
    {
        var byName = projects.ToDictionary(p => p.AssemblyName, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjInfo>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unseen,1=visiting,2=done

        void Visit(ProjInfo p)
        {
            state.TryGetValue(p.AssemblyName, out int s);
            if (s == 2) { return; }
            if (s == 1) { return; } // cycle guard (shouldn't happen)
            state[p.AssemblyName] = 1;
            foreach (var dep in p.SourceProjectRefs)
            {
                if (byName.TryGetValue(dep, out var depProj)) { Visit(depProj); }
            }
            state[p.AssemblyName] = 2;
            result.Add(p);
        }

        foreach (var p in projects) { Visit(p); }
        return result;
    }

    /// <summary>Splits a csc command line into individual arguments, honoring double quotes.</summary>
    public static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) { list.Add(sb.ToString()); }
        return list;
    }
}
