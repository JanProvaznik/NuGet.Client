// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace NuGet.Common
{
    /// <summary>
    /// Resets the process-global, environment-derived caches that NuGet.Common holds, so a host that reuses the
    /// process across builds (MSBuild Server / multithreaded MSBuild) re-reads them from the current environment
    /// at the start of the next restore.
    /// </summary>
    /// <remarks>
    /// This is the NuGet.Common "per-assembly reset" entry point. Rather than centralizing every environment read
    /// into a single shared type, each assembly keeps its reads where they are and exposes one reset method that
    /// the restore entry point invokes. The individual caches stay internal to their own types.
    /// </remarks>
    public static class NuGetCommonProcessState
    {
        /// <summary>Resets NuGet.Common's environment-derived caches.</summary>
        public static void ResetEnvironmentCaches()
        {
            ExceptionLogger.ResetInstance();
            ConcurrencyUtilities.ResetEnvironmentCaches();
            NuGetEnvironment.ResetEnvironmentCaches();
        }
    }
}
