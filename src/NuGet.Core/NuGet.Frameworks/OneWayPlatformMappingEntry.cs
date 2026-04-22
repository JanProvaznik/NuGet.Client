// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;

namespace NuGet.Frameworks
{
    /// <summary>
    /// Represents a one-way platform compatibility mapping for Net5Era+ TFMs.
    /// For example, the "winrt" platform supports packages targeting the "windows" platform.
    /// </summary>
    public class OneWayPlatformMappingEntry
    {
        /// <summary>
        /// The project's platform name that supports the <see cref="SupportedPlatform"/>.
        /// </summary>
        public required string TargetPlatform { get; init; }

        /// <summary>
        /// Minimum .NET framework version required for this mapping to apply, or null for no minimum.
        /// </summary>
        public Version? MinTargetFrameworkVersion { get; init; }

        /// <summary>
        /// The platform name that is supported by the <see cref="TargetPlatform"/>.
        /// </summary>
        public required string SupportedPlatform { get; init; }

        /// <summary>
        /// Maximum platform version on the supported side for this mapping to apply, or null for no maximum.
        /// </summary>
        public Version? MaxSupportedPlatformVersion { get; init; }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1}) -> {2} ({3})",
                TargetPlatform,
                MinTargetFrameworkVersion is not null ? ">=" + MinTargetFrameworkVersion : "*",
                SupportedPlatform,
                MaxSupportedPlatformVersion is not null ? "<=" + MaxSupportedPlatformVersion : "*");
        }
    }
}
