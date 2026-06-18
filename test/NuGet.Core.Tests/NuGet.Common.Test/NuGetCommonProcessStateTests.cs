// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Xunit;

namespace NuGet.Common.Test
{
    public class NuGetCommonProcessStateTests
    {
        [Fact]
        public void ResetEnvironmentCaches_RereadsShowStackFromEnvironment()
        {
            string original = Environment.GetEnvironmentVariable("NUGET_SHOW_STACK");
            try
            {
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", "true");
                NuGetCommonProcessState.ResetEnvironmentCaches();
                Assert.True(ExceptionLogger.Instance.ShowStack);

                // Simulate a process reused for a new build whose environment no longer sets the flag.
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", null);
                NuGetCommonProcessState.ResetEnvironmentCaches();
                Assert.False(ExceptionLogger.Instance.ShowStack);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", original);
                NuGetCommonProcessState.ResetEnvironmentCaches();
            }
        }
    }
}
