// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace NuGet.Credentials
{
    /// <summary>
    /// The NuGet.Credentials "per-assembly reset" entry point: re-reads its environment-derived caches so a
    /// reused process observes the current environment at the start of the next restore.
    /// </summary>
    public static class NuGetCredentialsProcessState
    {
        /// <summary>Resets NuGet.Credentials' environment-derived caches.</summary>
        public static void ResetEnvironmentCaches()
        {
            PreviewFeatureSettings.ResetCache();
        }
    }
}
