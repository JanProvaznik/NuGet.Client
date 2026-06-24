using System.Collections.Generic;

namespace RestoreStateAnalyzer;

internal enum SinkCategory
{
    ChildProcess,
    EnvVarWrite,
    EnvVarRead,
    CurrentDirectoryChange,
    CurrentDirectoryRead,
    PathGetFullPathAgainstCwd,
    ConsoleRedirection,
    CultureChange,
    RegistryWrite,
    AppContextMutation,
    ThreadPoolConfig,
    NetworkGlobalConfig,
    ProcessExit,
    ThreadCreation,
    TimerCreation,
    AppDomainHandler,
    StaticEventSubscription,
}

internal enum StaticStateKind
{
    MutableStaticField,
    StaticReadonlyMutableRef,
    StaticReadonlyImmutable,
    ThreadStaticField,
    ThreadLocalOrAsyncLocal,
    LazyStaticField,
    MutableStaticProperty,
    StaticEvent,
}

internal sealed class SinkFinding
{
    public required SinkCategory Category { get; init; }
    public required string Api { get; init; }
    public required string EnclosingMethod { get; init; }
    public required string Location { get; init; }
    public required string EnclosingAssembly { get; init; }
    public string EntryTask { get; set; } = "";
    public List<string> Path { get; set; } = new();
}

internal sealed class StaticStateFinding
{
    public required string RawKey { get; init; }
    public required string DeclaringAssembly { get; init; }
    public required string DeclaringType { get; init; }
    public required string Member { get; init; }
    public required string MemberType { get; init; }
    public StaticStateKind Kind { get; set; }
    public bool Read { get; set; }
    public bool Written { get; set; }
    public bool Initialized { get; set; }
    public string TypeShape { get; set; } = "other";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string EntryTask { get; set; } = "";
    public string AccessedFrom { get; set; } = "";
    public List<string> Path { get; set; } = new();
}
