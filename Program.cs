using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace RestoreStateAnalyzer;

internal static class Program
{
    // (metadata type name, friendly task name, group)
    private static readonly (string type, string task, string group)[] s_entryTasks =
    {
        // Regular `dotnet restore` path (RestoreTask + DG-spec collection tasks declared in NuGet.targets)
        ("NuGet.Build.Tasks.RestoreTask", "RestoreTask", "regular"),
        ("NuGet.Build.Tasks.WriteRestoreGraphTask", "WriteRestoreGraphTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreProjectReferencesTask", "GetRestoreProjectReferencesTask", "regular"),
        ("NuGet.Build.Tasks.GetRestorePackageReferencesTask", "GetRestorePackageReferencesTask", "regular"),
        ("NuGet.Build.Tasks.GetCentralPackageVersionsTask", "GetCentralPackageVersionsTask", "regular"),
        ("NuGet.Build.Tasks.GetRestorePackageDownloadsTask", "GetRestorePackageDownloadsTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreFrameworkReferencesTask", "GetRestoreFrameworkReferencesTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreNuGetAuditSuppressionsTask", "GetRestoreNuGetAuditSuppressionsTask", "regular"),
        ("NuGet.Build.Tasks.GetRestorePrunePackageReferencesTask", "GetRestorePrunePackageReferencesTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreDotnetCliToolsTask", "GetRestoreDotnetCliToolsTask", "regular"),
        ("NuGet.Build.Tasks.GetProjectTargetFrameworksTask", "GetProjectTargetFrameworksTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreSolutionProjectsTask", "GetRestoreSolutionProjectsTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreSettingsTask", "GetRestoreSettingsTask", "regular"),
        ("NuGet.Build.Tasks.WarnForInvalidProjectsTask", "WarnForInvalidProjectsTask", "regular"),
        ("NuGet.Build.Tasks.GetReferenceNearestTargetFrameworkTask", "GetReferenceNearestTargetFrameworkTask", "regular"),
        ("NuGet.Build.Tasks.GetRestoreProjectStyleTask", "GetRestoreProjectStyleTask", "regular"),
        ("NuGet.Build.Tasks.NuGetMessageTask", "NuGetMessageTask", "regular"),
        ("NuGet.Build.Tasks.CheckForDuplicateNuGetItemsTask", "CheckForDuplicateNuGetItemsTask", "regular"),
        ("NuGet.Build.Tasks.GetGlobalPropertyValueTask", "GetGlobalPropertyValueTask", "regular"),
        // Static-graph restore (opt-in: RestoreUseStaticGraphEvaluation=true) - out-of-proc entry tasks
        ("NuGet.Build.Tasks.RestoreTaskEx", "RestoreTaskEx", "static-graph"),
        ("NuGet.Build.Tasks.GenerateRestoreGraphFileTask", "GenerateRestoreGraphFileTask", "static-graph"),
    };

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: RestoreStateAnalyzer <path-to.binlog> [report.md] [probe ...]");
            Console.WriteLine("  Produce the binlog first, e.g.:");
            Console.WriteLine("    dotnet restore src/NuGet.Core/NuGet.Build.Tasks/NuGet.Build.Tasks.csproj");
            Console.WriteLine("    dotnet build   src/NuGet.Core/NuGet.Build.Tasks/NuGet.Build.Tasks.csproj -f net10.0 --no-restore -bl:restore.binlog");
            return 1;
        }
        string binlog = args[0];
        string reportPath = args.Length > 1 ? args[1] : "report.md";

        Console.WriteLine("Reconstructing compilations from binlog...");
        var set = CompilationSet.BuildFromBinlog(binlog);
        Console.WriteLine($"Source assemblies: {set.Projects.Count}");

        int totalErr = 0;
        foreach (var p in set.Projects)
        {
            var errs = p.Compilation!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS7027")
                .ToList();
            totalErr += errs.Count;
            Console.WriteLine($"  {p.AssemblyName,-32} trees={p.Trees.Count,4} realErrors={errs.Count}");
            foreach (var grp in errs.GroupBy(e => e.Id).OrderByDescending(g => g.Count()))
            {
                string msg = grp.First().GetMessage();
                Console.WriteLine($"       {grp.Key} x{grp.Count()} :: {msg.Substring(0, Math.Min(130, msg.Length))}");
            }
        }
        Console.WriteLine($"Total real binding errors across reconstructed compilations: {totalErr}");

        string? analyzerDll = args.FirstOrDefault(a => a.StartsWith("--msbuild-analyzer=", StringComparison.Ordinal))?.Substring("--msbuild-analyzer=".Length);
        if (analyzerDll is not null)
        {
            Console.WriteLine("\n==== Running external MSBuild TaskAnalyzer (per-compilation) ====\n");
            MsBuildAnalyzerRunner.Run(set, analyzerDll);
            return 0;
        }

        string? envSha = args.FirstOrDefault(a => a.StartsWith("--env-statics=", StringComparison.Ordinal))?.Substring("--env-statics=".Length);
        if (envSha is not null)
        {
            Console.WriteLine("\n==== Cached environment-variable statics ====\n");
            var findings = new EnvStaticsAnalyzer(set).Run();
            Console.WriteLine($"Found {findings.Count} cached env-var static members.\n");
            const string repo = "https://github.com/JanProvaznik/NuGet.Client/blob";
            foreach (var g in findings.GroupBy(f => f.Assembly).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"### {g.Key}");
                foreach (var f in g)
                {
                    Console.WriteLine($"- [`{f.Member}`]({repo}/{envSha}/{f.File}#L{f.Line}) : `{f.MemberType}` — {f.How}{(string.IsNullOrEmpty(f.Vars) ? "" : $" (`{f.Vars}`)")}");
                }
                Console.WriteLine();
            }
            return 0;
        }

        var analyzer = new Analyzer(set);
        Console.WriteLine("Building cross-assembly override/implementation map...");
        analyzer.BuildOverrideMap();
        Console.WriteLine("Seeding entry points...");
        analyzer.SeedEntryPoints(s_entryTasks);
        Console.WriteLine("Running interprocedural reachability...");
        analyzer.Run();
        Console.WriteLine($"Reachable methods/nodes: {analyzer.Display.Count}");

        string? annSha = args.FirstOrDefault(a => a.StartsWith("--annotate-statics=", StringComparison.Ordinal))?.Substring("--annotate-statics=".Length);
        if (annSha is not null)
        {
            Console.WriteLine("\n==== Annotating reachable static members (resetability verdicts) ====\n");
            string ann = new StaticAnnotator(analyzer, set, annSha).Build();
            File.WriteAllText(reportPath, ann);
            Console.WriteLine($"Annotation written to {reportPath}");
            return 0;
        }

        var report = new Reporter(set, analyzer, s_entryTasks);
        string md = report.Build(totalErr);
        File.WriteAllText(reportPath, md);
        Console.WriteLine($"\nReport written to {reportPath}");
        report.PrintConsoleSummary();

        foreach (var probe in args.Skip(2))
        {
            if (probe.StartsWith("MAP:", StringComparison.Ordinal))
            {
                Console.WriteLine($"\n==== OVERRIDE MAP: '{probe.Substring(4)}' ====");
                analyzer.DumpOverrides(probe.Substring(4));
            }
            else
            {
                Console.WriteLine($"\n==== PROBE: '{probe}' ====");
                report.Probe(probe);
            }
        }
        return 0;
    }
}
