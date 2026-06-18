// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace NuGet.Build.Tasks
{
    /// <summary>
    /// Resets NuGet's process-global, environment-derived caches at the start of a restore by invoking each
    /// NuGet assembly's per-assembly reset entry point. This runs as the first task in the restore target chain
    /// (see the <c>_NuGetResetProcessState</c> target) so that a host which reuses the process across builds
    /// (MSBuild Server / multithreaded MSBuild) observes the current environment for this restore rather than a
    /// value frozen by an earlier build.
    /// </summary>
    /// <remarks>
    /// This is the coordinator for the "per-assembly reset" design: each owning assembly keeps its environment
    /// reads where they are and exposes one public reset method; this task calls them. Adding another assembly's
    /// caches to the reset is a one-line addition here plus a reset method in that assembly.
    /// </remarks>
    public sealed class ResetNuGetProcessStateTask : Microsoft.Build.Utilities.Task
    {
        public override bool Execute()
        {
            NuGet.Common.NuGetCommonProcessState.ResetEnvironmentCaches();
            NuGet.Protocol.NuGetProtocolProcessState.ResetEnvironmentCaches();
            NuGet.Credentials.NuGetCredentialsProcessState.ResetEnvironmentCaches();
            NuGet.ProjectModel.NuGetProjectModelProcessState.ResetEnvironmentCaches();
            NuGet.Commands.NuGetCommandsProcessState.ResetEnvironmentCaches();

            return true;
        }
    }
}
