// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace NuGet.Commands
{
    /// <summary>
    /// The NuGet.Commands "per-assembly reset" entry point: re-reads its environment-derived caches so a reused
    /// process observes the current environment at the start of the next restore. The caller must ensure no
    /// restore is in flight.
    /// </summary>
    public static class NuGetCommandsProcessState
    {
        /// <summary>Resets NuGet.Commands' environment-derived caches.</summary>
        public static void ResetEnvironmentCaches()
        {
            SourceRepositoryDependencyProvider.ResetCache();
        }
    }
}
