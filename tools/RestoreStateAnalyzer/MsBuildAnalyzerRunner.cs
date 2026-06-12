using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RestoreStateAnalyzer;

/// <summary>
/// Loads an external Roslyn analyzer assembly (e.g. MSBuild's TaskAnalyzer built from
/// dotnet/msbuild/src/TaskAnalyzer) and runs every <see cref="DiagnosticAnalyzer"/> it contains against
/// each reconstructed NuGet compilation, to show exactly what the per-compilation analyzer reports - and,
/// by contrast with the interprocedural pass, what it cannot see across assembly boundaries.
/// </summary>
internal static class MsBuildAnalyzerRunner
{
    public static void Run(CompilationSet set, string analyzerDllPath)
    {
        Assembly asm = Assembly.LoadFrom(analyzerDllPath);
        var analyzers = asm.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
            .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToImmutableArray();

        Console.WriteLine($"Loaded analyzer assembly: {Path.GetFileName(analyzerDllPath)}");
        Console.WriteLine($"Analyzers: {string.Join(", ", analyzers.Select(a => a.GetType().Name))}");
        Console.WriteLine("(default scope = 'all' => every ITask implementation is analyzed; MSBuildTask0005 traces transitively within the compilation)\n");

        var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty);

        foreach (var p in set.Projects.OrderBy(p => p.AssemblyName, StringComparer.Ordinal))
        {
            var comp = p.Compilation!;
            ImmutableArray<Diagnostic> diags = comp
                .WithAnalyzers(analyzers, options)
                .GetAnalyzerDiagnosticsAsync()
                .GetAwaiter().GetResult();

            var msb = diags.Where(d => d.Id.StartsWith("MSBuildTask", StringComparison.Ordinal)).ToList();
            var crashed = diags.Where(d => d.Id == "AD0001").ToList();
            Console.WriteLine($"=== {p.AssemblyName,-32} {msb.Count} MSBuildTask diagnostic(s); {crashed.Count} analyzer-crash (AD0001) ===");
            foreach (var c in crashed.Take(2))
            {
                string m = c.GetMessage();
                Console.WriteLine($"      AD0001: {m.Substring(0, Math.Min(300, m.Length))}");
            }
            foreach (var g in msb.GroupBy(d => d.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"      {g.Key} x{g.Count()}");
            }

            if (msb.Count > 0)
            {
                foreach (var d in msb.OrderBy(d => d.Id, StringComparer.Ordinal)
                                     .ThenBy(d => d.Location.GetLineSpan().Path, StringComparer.Ordinal)
                                     .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line))
                {
                    var ls = d.Location.GetLineSpan();
                    Console.WriteLine($"        {d.Id}  {Path.GetFileName(ls.Path)}:{ls.StartLinePosition.Line + 1}  {d.GetMessage()}");
                }
            }
            Console.WriteLine();
        }
    }
}
