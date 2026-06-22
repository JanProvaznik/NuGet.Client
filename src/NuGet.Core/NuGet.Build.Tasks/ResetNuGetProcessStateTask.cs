// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Build.Framework;

namespace NuGet.Build.Tasks
{
    /// <summary>
    /// Executes the NuGet process-state reset actions registered under a given key. Wired into the restore target
    /// chain twice: with <c>StartBuild</c> as the first task (re-read environment-derived caches for this restore)
    /// and with <c>EndRestore</c> after the restore (tear down plugin processes so they do not outlive the build).
    /// </summary>
    /// <remarks>
    /// The task is generic: it only calls <see cref="NuGet.Common.NuGetProcessState.Reset" /> with
    /// <see cref="Key" />. Each cache/resource self-registers its action with the registry, so adding more state
    /// to reset never changes this task.
    /// </remarks>
    public sealed class ResetNuGetProcessStateTask : Microsoft.Build.Utilities.Task
    {
        /// <summary>The reset group key, for example <c>StartBuild</c> or <c>EndRestore</c>.</summary>
        [Required]
        public string Key { get; set; } = string.Empty;

        public override bool Execute()
        {
            NuGet.Common.NuGetProcessState.Reset(Key);

            return true;
        }
    }
}
