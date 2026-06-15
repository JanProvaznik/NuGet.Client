// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using NuGet.Common;

namespace NuGet.Protocol.Core.Types
{
    public static class NuGetTestMode
    {
        public const string NuGetTestClientName = "NuGet Test Client";

        static NuGetTestMode()
        {
            // Cached for the life-time of the app domain via the resettable NuGetTraits singleton.
            Enabled = NuGetTraits.Instance.TestModeEnabled;
        }

        public static bool Enabled { get; private set; }


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
