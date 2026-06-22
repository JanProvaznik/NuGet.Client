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
            string original = Environment.GetEnvironmentVariable("NUGET_SHOW_STACK");
            try
            {
                // Touch ExceptionLogger so its static constructor registers its reset under StartBuild.
                _ = ExceptionLogger.Instance;

                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", "true");
                NuGetProcessState.Reset(NuGetProcessState.StartBuild);
                Assert.True(ExceptionLogger.Instance.ShowStack);

                // Simulate a process reused for a new build whose environment no longer sets the flag.
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", null);
                NuGetProcessState.Reset(NuGetProcessState.StartBuild);
                Assert.False(ExceptionLogger.Instance.ShowStack);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NUGET_SHOW_STACK", original);
                NuGetProcessState.Reset(NuGetProcessState.StartBuild);
            }
        }

        [Fact]
        public void Reset_RunsAllActionsRegisteredUnderKey_AndIgnoresOtherKeys()
        {
            string key = "NuGetProcessStateTests." + Guid.NewGuid().ToString("N");
            int count = 0;
            NuGetProcessState.RegisterResetAction(key, () => count++);
            NuGetProcessState.RegisterResetAction(key, () => count++); // keyed list: both run

            NuGetProcessState.Reset(key);
            Assert.Equal(2, count);

            // A different (unused) key is a no-op.
            NuGetProcessState.Reset("NuGetProcessStateTests." + Guid.NewGuid().ToString("N"));
            Assert.Equal(2, count);
        }

        [Fact]
        public void Reset_OneFailingAction_StillRunsTheOthers()
        {
            string key = "NuGetProcessStateTests." + Guid.NewGuid().ToString("N");
            bool ranSecond = false;
            NuGetProcessState.RegisterResetAction(key, () => throw new InvalidOperationException("boom"));
            NuGetProcessState.RegisterResetAction(key, () => ranSecond = true);

            NuGetProcessState.Reset(key);

            Assert.True(ranSecond);
        }
    }
}
