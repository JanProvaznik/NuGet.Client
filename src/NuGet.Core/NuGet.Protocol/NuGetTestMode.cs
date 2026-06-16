// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using NuGet.Common;

namespace NuGet.Protocol.Core.Types
{
    public static class NuGetTestMode
    {
        public const string NuGetTestClientName = "NuGet Test Client";

        private static bool? _testModeOverride;

        /// <summary>
        /// Whether NuGet is running in test mode (env <c>NuGetTestModeEnabled</c>), read live from the resettable
        /// <see cref="NuGetTraits" /> so a reused process observes the current value. A test may temporarily
        /// override it via <see cref="InvokeTestFunctionAgainstTestMode{T}" />.
        /// </summary>
        public static bool Enabled
        {
            get => _testModeOverride ?? NuGetTraits.Instance.TestModeEnabled;
            private set => _testModeOverride = value;
        }


        /// <summary>
        /// Intended for internal use only: utility method for testing purposes.
        /// </summary>
        public static T InvokeTestFunctionAgainstTestMode<T>(Func<T> function, bool testModeEnabled)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            var valueBeforeTestRun = Enabled;

            Enabled = testModeEnabled;

            var result = function();

            Enabled = valueBeforeTestRun;

            return result;
        }
    }
}
