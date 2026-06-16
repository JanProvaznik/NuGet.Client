// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;

namespace NuGet.Common
{
    /// <summary>
    /// Centralized, resettable access to NuGet's environment-variable-derived engine settings, modeled on
    /// MSBuild's <c>Traits</c> class. Each setting is read once per instance through an
    /// <see cref="IEnvironmentVariableReader" />; call <see cref="UpdateFromEnvironment()" /> to recreate the
    /// instance from the current environment.
    /// </summary>
    /// <remarks>
    /// Lives in NuGet.Common (the lowest common NuGet assembly) so every higher assembly can consume it instead
    /// of caching environment variables in its own statics. In a process reused across builds (MSBuild Server /
    /// multithreaded MSBuild) the end-of-build cleanup calls <see cref="UpdateFromEnvironment()" /> so the next
    /// build observes the current environment; tests use the reader-accepting overload to supply their own
    /// environment source. Subsequent changes add one strongly-typed setting per migrated cached static.
    /// </remarks>
    public sealed class NuGetTraits
    {
        private static NuGetTraits _instance = new NuGetTraits(EnvironmentVariableWrapper.Instance);

        /// <summary>The process-wide traits. Cached; recreate with <see cref="UpdateFromEnvironment()" />.</summary>
        public static NuGetTraits Instance => Volatile.Read(ref _instance);

        internal NuGetTraits(IEnvironmentVariableReader env)
        {
            UseSystemTextJsonDeserialization = string.Equals(
                env.GetEnvironmentVariable("NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION"), "true", StringComparison.OrdinalIgnoreCase);

            TestModeEnabled = bool.TryParse(env.GetEnvironmentVariable("NuGetTestModeEnabled"), out bool testMode) && testMode;

            PackageIdValidationDisabled = string.Equals(
                env.GetEnvironmentVariable("NUGET_DISABLE_PACKAGEID_VALIDATION"), bool.TrueString, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Env: <c>NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION</c>.</summary>
        public bool UseSystemTextJsonDeserialization { get; }

        /// <summary>Env: <c>NuGetTestModeEnabled</c>.</summary>
        public bool TestModeEnabled { get; }

        /// <summary>Env: <c>NUGET_DISABLE_PACKAGEID_VALIDATION</c>.</summary>
        public bool PackageIdValidationDisabled { get; }

        /// <summary>Recreate the shared <see cref="Instance" /> from the current environment.</summary>
        public static void UpdateFromEnvironment() => UpdateFromEnvironment(EnvironmentVariableWrapper.Instance);

        /// <summary>
        /// Recreate the shared <see cref="Instance" /> from the supplied environment source. Used by the
        /// end-of-build cleanup to re-read settings in a reused process, and by tests.
        /// </summary>
        public static void UpdateFromEnvironment(IEnvironmentVariableReader env) => Volatile.Write(ref _instance, new NuGetTraits(env));
    }
}
