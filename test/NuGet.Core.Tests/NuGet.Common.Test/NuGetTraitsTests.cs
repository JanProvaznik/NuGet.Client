// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using Xunit;

namespace NuGet.Common.Test
{
    public class NuGetTraitsTests
    {
        private sealed class DictionaryEnvReader : IEnvironmentVariableReader
        {
            private readonly Dictionary<string, string> _values;
            public DictionaryEnvReader(Dictionary<string, string> values) => _values = values;
            public string? GetEnvironmentVariable(string variable) => _values.TryGetValue(variable, out var v) ? v : null;
        }

        [Fact]
        public void Instance_DefaultsToFalse_WhenEnvironmentUnset()
        {
            NuGetTraits.UpdateFromEnvironment(new DictionaryEnvReader(new Dictionary<string, string>()));

            Assert.False(NuGetTraits.Instance.UseSystemTextJsonDeserialization);
            Assert.False(NuGetTraits.Instance.TestModeEnabled);
            Assert.False(NuGetTraits.Instance.PackageIdValidationDisabled);
            Assert.False(NuGetTraits.Instance.DefaultCredentialsAfterCredentialProviders);
        }

        [Fact]
        public void Instance_ReadsFlags_FromEnvironment()
        {
            NuGetTraits.UpdateFromEnvironment(new DictionaryEnvReader(new Dictionary<string, string>
            {
                ["NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION"] = "true",
                ["NuGetTestModeEnabled"] = "true",
                ["NUGET_DISABLE_PACKAGEID_VALIDATION"] = "True",
                ["NUGET_CREDENTIAL_PROVIDER_OVERRIDE_DEFAULT"] = "true",
            }));

            Assert.True(NuGetTraits.Instance.UseSystemTextJsonDeserialization);
            Assert.True(NuGetTraits.Instance.TestModeEnabled);
            Assert.True(NuGetTraits.Instance.PackageIdValidationDisabled);
            Assert.True(NuGetTraits.Instance.DefaultCredentialsAfterCredentialProviders);
        }

        [Fact]
        public void UpdateFromEnvironment_Resets_OnChangedEnvironment()
        {
            NuGetTraits.UpdateFromEnvironment(new DictionaryEnvReader(new Dictionary<string, string>
            {
                ["NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION"] = "true",
            }));
            Assert.True(NuGetTraits.Instance.UseSystemTextJsonDeserialization);

            // Simulate a process reused for a new build whose environment no longer sets the flag.
            NuGetTraits.UpdateFromEnvironment(new DictionaryEnvReader(new Dictionary<string, string>()));
            Assert.False(NuGetTraits.Instance.UseSystemTextJsonDeserialization);

            // Restore process default so other tests are unaffected.
            NuGetTraits.UpdateFromEnvironment();
        }
    }
}
