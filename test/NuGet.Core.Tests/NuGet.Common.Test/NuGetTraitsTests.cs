// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Xunit;

namespace NuGet.Common.Test
{
    public class NuGetTraitsTests
    {
        [Fact]
        public void UpdateFromEnvironment_ReplacesInstance()
        {
            NuGetTraits before = NuGetTraits.Instance;

            NuGetTraits.UpdateFromEnvironment();

            Assert.NotSame(before, NuGetTraits.Instance);
        }
    }
}
