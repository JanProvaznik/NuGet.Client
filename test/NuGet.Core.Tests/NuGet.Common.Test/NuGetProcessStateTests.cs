// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Xunit;

namespace NuGet.Common.Test
{
    public class NuGetProcessStateTests
    {
        [Fact]
        public void Reset_StartBuild_RereadsShowStackFromEnvironment()
        {
            string? original = Environment.GetEnvironmentVariable("NUGET_SHOW_STACK");
            try
            {
                // Touch ExceptionLogger so its static constructor registers its reset for StartBuild.
                _ = ExceptionLogger.Instance;

                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", "true");
                NuGetProcessState.Reset(NuGetProcessState.ResetKey.StartBuild);
                Assert.True(ExceptionLogger.Instance.ShowStack);

                // Simulate a process reused for a new build whose environment no longer sets the flag.
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", null);
                NuGetProcessState.Reset(NuGetProcessState.ResetKey.StartBuild);
                Assert.False(ExceptionLogger.Instance.ShowStack);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", original);
                NuGetProcessState.Reset(NuGetProcessState.ResetKey.StartBuild);
            }
        }

        [Fact]
        public void Reset_RunsAllActionsForKey_AndIsolatesFailures()
        {
            int ran = 0;
            NuGetProcessState.RegisterResetAction(NuGetProcessState.ResetKey.StartBuild, () => throw new InvalidOperationException("boom"));
            NuGetProcessState.RegisterResetAction(NuGetProcessState.ResetKey.StartBuild, () => ran++);

            NuGetProcessState.Reset(NuGetProcessState.ResetKey.StartBuild);

            // The throwing action did not prevent the counting action from running.
            Assert.True(ran >= 1);
        }
    }
}
