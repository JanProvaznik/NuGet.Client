using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace RestoreStateAnalyzer;

/// <summary>
/// Classifies calls / property writes / object creations / event subscriptions against
/// process-global ("lingering") state sinks.
/// </summary>
internal static class SinkRules
{
    // Types whose construction is itself a "child process / lingering resource" sink.
    private static readonly Dictionary<string, SinkCategory> s_creationSinks = new(StringComparer.Ordinal)
    {
        ["System.Diagnostics.Process"] = SinkCategory.ChildProcess,
        ["System.Diagnostics.ProcessStartInfo"] = SinkCategory.ChildProcess,
        ["System.Threading.Thread"] = SinkCategory.ThreadCreation,
        ["System.Threading.Timer"] = SinkCategory.TimerCreation,
        ["System.Timers.Timer"] = SinkCategory.TimerCreation,
    };

    public static bool TryClassifyInvocation(IMethodSymbol m, out SinkCategory category, out string api)
    {
        category = default;
        api = "";
        string type = TypeName(m.ContainingType);
        string name = m.Name;

        switch (type)
        {
            case "System.Environment" when name == "SetEnvironmentVariable":
                category = SinkCategory.EnvVarWrite; api = "System.Environment.SetEnvironmentVariable"; return true;
            case "System.Environment" when name is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "ExpandEnvironmentVariables":
                category = SinkCategory.EnvVarRead; api = "System.Environment." + name; return true;
            case "System.Environment" when name is "Exit" or "FailFast":
                category = SinkCategory.ProcessExit; api = "System.Environment." + name; return true;
            case "System.IO.Directory" when name == "SetCurrentDirectory":
                category = SinkCategory.CurrentDirectoryChange; api = "System.IO.Directory.SetCurrentDirectory"; return true;
            case "System.AppContext" when name is "SetSwitch" or "SetData":
                category = SinkCategory.AppContextMutation; api = "System.AppContext." + name; return true;
            case "System.Console" when name is "SetOut" or "SetError" or "SetIn":
                category = SinkCategory.ConsoleRedirection; api = "System.Console." + name; return true;
            case "System.Threading.ThreadPool" when name is "SetMinThreads" or "SetMaxThreads":
                category = SinkCategory.ThreadPoolConfig; api = "System.Threading.ThreadPool." + name; return true;
            case "System.Diagnostics.Process" when name is "Start" or "Kill":
                category = SinkCategory.ChildProcess; api = "System.Diagnostics.Process." + name; return true;
        }

        if ((type == "Microsoft.Win32.RegistryKey" || type == "Microsoft.Win32.Registry")
            && name is "SetValue" or "DeleteValue" or "CreateSubKey" or "DeleteSubKey" or "DeleteSubKeyTree")
        {
            category = SinkCategory.RegistryWrite; api = type + "." + name; return true;
        }

        return false;
    }

    public static bool TryClassifyPropertyWrite(IPropertySymbol p, out SinkCategory category, out string api)
    {
        category = default;
        api = "";
        string type = TypeName(p.ContainingType);
        string name = p.Name;

        switch (type)
        {
            case "System.Environment" when name == "CurrentDirectory":
                category = SinkCategory.CurrentDirectoryChange; api = "System.Environment.CurrentDirectory (set)"; return true;
            case "System.Globalization.CultureInfo" when name is "CurrentCulture" or "CurrentUICulture" or "DefaultThreadCurrentCulture" or "DefaultThreadCurrentUICulture":
                category = SinkCategory.CultureChange; api = "System.Globalization.CultureInfo." + name + " (set)"; return true;
            case "System.Threading.Thread" when name is "CurrentCulture" or "CurrentUICulture":
                category = SinkCategory.CultureChange; api = "System.Threading.Thread." + name + " (set)"; return true;
            case "System.Console" when name is "OutputEncoding" or "InputEncoding" or "Out" or "Error" or "In" or "ForegroundColor" or "BackgroundColor":
                category = SinkCategory.ConsoleRedirection; api = "System.Console." + name + " (set)"; return true;
            case "System.Net.ServicePointManager":
                category = SinkCategory.NetworkGlobalConfig; api = "System.Net.ServicePointManager." + name + " (set)"; return true;
        }

        return false;
    }

    public static bool TryClassifyCreation(INamedTypeSymbol? type, out SinkCategory category, out string api)
    {
        category = default;
        api = "";
        if (type is null) { return false; }
        string name = TypeName(type);
        if (s_creationSinks.TryGetValue(name, out category))
        {
            api = "new " + name + "()";
            return true;
        }
        return false;
    }

    public static bool TryClassifyEvent(IEventSymbol e, out SinkCategory category, out string api)
    {
        category = default;
        api = "";
        string type = TypeName(e.ContainingType);
        if (type == "System.AppDomain")
        {
            category = SinkCategory.AppDomainHandler;
            api = "System.AppDomain." + e.Name + " (subscribe)";
            return true;
        }
        if (e.IsStatic)
        {
            category = SinkCategory.StaticEventSubscription;
            api = type + "." + e.Name + " (subscribe)";
            return true;
        }
        return false;
    }

    private static readonly SymbolDisplayFormat s_typeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    public static string TypeName(INamedTypeSymbol? t) => t is null ? "" : t.OriginalDefinition.ToDisplayString(s_typeFormat);
}
