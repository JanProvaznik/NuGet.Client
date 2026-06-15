// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable enable

using FluentAssertions;
using Xunit;

namespace NuGet.Build.Tasks.Test
{
    public class TaskEnvironmentVariableReaderTests
    {
        [Fact]
        public void GetEnvironmentVariable_DelegatesToFunc()
        {
            var reader = new TaskEnvironmentVariableReader(name => name == "FOO" ? "bar" : null);

            reader.GetEnvironmentVariable("FOO").Should().Be("bar");
            reader.GetEnvironmentVariable("MISSING").Should().BeNull();
        }

        [Fact]
        public void NuGetTraits_CanBeResetFromTaskEnvironmentReader()
        {
            // Simulate a task that re-reads NuGet traits from its (isolated) environment at the start of a build.
            var reader = new TaskEnvironmentVariableReader(name =>
                name == "NUGET_USE_SYSTEM_TEXT_JSON_DESERIALIZATION" ? "true" : null);

            NuGet.Common.NuGetTraits.UpdateFromEnvironment(reader);
            try
            {
                NuGet.Common.NuGetTraits.Instance.UseSystemTextJsonDeserialization.Should().BeTrue();
            }
            finally
            {
                NuGet.Common.NuGetTraits.UpdateFromEnvironment();
            }
        }
    }
}
