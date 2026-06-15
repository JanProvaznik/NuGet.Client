// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using NuGet.Common;

namespace NuGet.Build.Tasks
{
    /// <summary>
    /// An <see cref="IEnvironmentVariableReader" /> backed by an arbitrary lookup delegate. It exists so that an
    /// enlightened (multithreaded) MSBuild task can feed NuGet's environment reads from its own
    /// <c>TaskEnvironment</c> instead of the process-global environment:
    /// <code>
    /// // when RestoreTask implements IMultiThreadableTask:
    /// var reader = new TaskEnvironmentVariableReader(TaskEnvironment.GetEnvironmentVariable);
    /// NuGetTraits.UpdateFromEnvironment(reader);
    /// </code>
    /// MSBuild's <c>TaskEnvironment</c> type is not referenced here (it is not yet in the shipping
    /// Microsoft.Build.Framework), so the adapter takes a delegate; the real wiring binds
    /// <c>TaskEnvironment.GetEnvironmentVariable</c> once that API is available. No reflection is used.
    /// </summary>
    internal sealed class TaskEnvironmentVariableReader : IEnvironmentVariableReader
    {
        private readonly Func<string, string?> _getEnvironmentVariable;

        public TaskEnvironmentVariableReader(Func<string, string?> getEnvironmentVariable)
        {
            _getEnvironmentVariable = getEnvironmentVariable ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
        }

        public string? GetEnvironmentVariable(string variable) => _getEnvironmentVariable(variable);
    }
}
