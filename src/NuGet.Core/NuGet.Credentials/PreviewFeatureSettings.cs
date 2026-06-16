// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable disable

using NuGet.Common;

namespace NuGet.Credentials
{
    /// <summary>
    /// Settings for in-flight features not ready to be turned on permanently
    /// </summary>
    public static class PreviewFeatureSettings
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public const string DefaultCredentialsAfterCredentialProvidersEnvironmentVariableName
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
            = "NUGET_CREDENTIAL_PROVIDER_OVERRIDE_DEFAULT";

        internal static IEnvironmentVariableReader environmentVariableReader { get; set; } = EnvironmentVariableWrapper.Instance;

        private static bool? _defaultCredentialsAfterCredentialProvidersOverride;

        /// <summary>
        /// Use DefaultNetworkCredentialsCredentialProvider after plugin credential providers to handle using the user's
        /// ambient Windows credentials, instead of support baked into HttpSourceCredentials. Read live from the
        /// resettable <see cref="NuGetTraits" /> unless explicitly overridden.
        /// </summary>
        public static bool DefaultCredentialsAfterCredentialProviders
        {
            get => _defaultCredentialsAfterCredentialProvidersOverride ?? NuGetTraits.Instance.DefaultCredentialsAfterCredentialProviders;
            set => _defaultCredentialsAfterCredentialProvidersOverride = value;
        }
    }
}
