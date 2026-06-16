// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using Microsoft.Build.Framework;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;
using NuGet.Protocol.Plugins;

namespace NuGet.Build.Tasks
{
    /// <summary>
    /// An <see cref="IDisposable" /> registered with MSBuild for the duration of a build (via
    /// <see cref="IBuildEngine4.RegisterTaskObject" /> with <see cref="RegisteredTaskObjectLifetime.Build" />).
    /// MSBuild disposes it at the end of the build, at which point it tears down the process-global state that
    /// restore would otherwise leave behind in a reused host process (MSBuild Server / multithreaded MSBuild),
    /// so the next build behaves as if it ran in a fresh process.
    /// </summary>
    /// <remarks>
    /// Every reset is ownership-aware - it only tears down state restore itself created - so externally owned
    /// state (for example a credential service set by Visual Studio in its own process) is never affected.
    /// </remarks>
    internal sealed class RestoreProcessStateCleanup : IDisposable
    {
        // A stable, process-wide key; registration is scoped per-build by MSBuild, so the same key yields a
        // separate registration (and disposal) for each build.
        private static readonly object RegistrationKey = typeof(RestoreProcessStateCleanup);
        private static readonly object RegistrationLock = new object();

        private RestoreProcessStateCleanup()
        {
        }

        /// <summary>
        /// Ensures exactly one cleanup token is registered for the current build. Safe to call from every
        /// restore task invocation; subsequent calls within the same build are no-ops.
        /// </summary>
        internal static void EnsureRegistered(IBuildEngine4 buildEngine)
        {
            if (buildEngine == null)
            {
                return;
            }

            lock (RegistrationLock)
            {
                if (buildEngine.GetRegisteredTaskObject(RegistrationKey, RegisteredTaskObjectLifetime.Build) == null)
                {
                    buildEngine.RegisterTaskObject(
                        RegistrationKey,
                        new RestoreProcessStateCleanup(),
                        RegisteredTaskObjectLifetime.Build,
                        allowEarlyCollection: false);
                }
            }
        }

        public void Dispose()
        {
            Reset();
        }

        /// <summary>
        /// Resets the process-global state restore created. Each step is best-effort and isolated: the build
        /// is over, so the task logger is no longer guaranteed to be valid, and a failure in one reset must not
        /// prevent the others.
        /// </summary>
        internal static void Reset()
        {
            // Credential service first: it transitively references the secure-plugin credential providers, so
            // it should stop pointing at the plugins before the plugin manager disposes them.
            TryReset(static () => DefaultCredentialServiceUtility.ResetDefaultCredentialService());

            // Plugin manager: disposes cached plugin processes and their idle/keep-alive timers.
            TryReset(static () => PluginManager.ResetSharedInstance());

            // Proxy cache: re-read proxy/credential settings on the next build.
            TryReset(static () => ProxyCache.ResetSharedInstance());

            // Environment-variable traits: re-read env-derived flags so a reused process observes the current
            // environment on the next build (see the NuGetTraits proposal).
            TryReset(static () => NuGet.Common.NuGetTraits.UpdateFromEnvironment());

            // Environment-derived path caches: home directory, NuGet temp directory, resolved folder paths, and
            // the lock-file base path (which derives from the temp directory).
            TryReset(static () => NuGet.Common.NuGetEnvironment.ResetCache());
            TryReset(static () => NuGet.Common.ConcurrencyUtilities.ResetCache());

            // Concurrency throttle sized from NUGET_CONCURRENCY_LIMIT: recreate from the current environment.
            TryReset(static () => SourceRepositoryDependencyProvider.ResetThrottle());

            // Per-process HTTP request throttle: clear any throttle a host set for the previous build.
            TryReset(static () => HttpSourceResourceProvider.Throttle = null);

            // NOTE (intentionally not reset here):
            //  - The per-source HttpSource handler cache is owned by the per-restore CachingSourceProvider
            //    (RestoreArgs.CachingSourceProvider), not a static, so it is reclaimed by GC after the restore;
            //    promptly disposing those sockets is a separate restore-flow optimization, not static state.
            //  - ExceptionLogger now reads NUGET_SHOW_STACK live from NuGetTraits, so it needs no reset here.
            //  - PluginLogger (NUGET_PLUGIN_ENABLE_LOG) only allocates a writer in the rare debug-logging case and
            //    holds nothing otherwise; X509ChainBuildPolicyFactory caches an experimental signing-retry policy
            //    with negligible staleness impact. Both are left as documented low-value follow-ups.
            //  - [ThreadStatic] resolver scratch buffers are per-thread and cleared on use; they only pin
            //    memory on pooled threads.
        }

        private static void TryReset(Action reset)
        {
            try
            {
                reset();
            }
#pragma warning disable CA1031 // Do not catch general exception types - end-of-build cleanup must be best-effort and isolated.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Debug.WriteLine($"RestoreProcessStateCleanup reset failed: {ex}");
            }
        }
    }
}
