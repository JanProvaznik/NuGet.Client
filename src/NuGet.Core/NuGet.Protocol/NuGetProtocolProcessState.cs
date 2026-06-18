// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable enable

using NuGet.Protocol.Core.Types;
using NuGet.Shared;

namespace NuGet.Protocol
{
    /// <summary>
    /// The NuGet.Protocol "per-assembly reset" entry point: resets the process-global, environment-derived caches
    /// that NuGet.Protocol holds, so a host that reuses the process across builds re-reads them from the current
    /// environment at the start of the next restore. The individual caches stay internal to their own types.
    /// </summary>
    public static class NuGetProtocolProcessState
    {
        /// <summary>Resets NuGet.Protocol's environment-derived caches.</summary>
        public static void ResetEnvironmentCaches()
        {
            NuGetFeatureFlags.ResetCache();
            NuGetTestMode.ResetCache();
            PackageIdValidator.ResetCache();
        }
    }
}
