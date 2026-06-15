// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable disable

using System;
using FluentAssertions;
using Microsoft.Build.Framework;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol;
using Xunit;

namespace NuGet.Build.Tasks.Test
{
    public class RestoreProcessStateCleanupTests
    {
        [Fact]
        public void EnsureRegistered_RegistersExactlyOneBuildLifetimeDisposable()
        {
            var engine = new TestBuildEngine();

            RestoreProcessStateCleanup.EnsureRegistered(engine);
            RestoreProcessStateCleanup.EnsureRegistered(engine);

            engine.RegisteredTaskObjects.Should().ContainSingle();
            var entry = Assert.Single(engine.RegisteredTaskObjects);
            entry.Key.Lifetime.Should().Be(RegisteredTaskObjectLifetime.Build);
            entry.Value.Should().BeAssignableTo<IDisposable>();
        }

        [Fact]
        public void EnsureRegistered_WhenBuildEngineIsNull_DoesNotThrow()
        {
            Action act = () => RestoreProcessStateCleanup.EnsureRegistered(null);

            act.Should().NotThrow();
        }

        [Fact]
        public void RestoreTask_WhenResetProcessStateAfterBuildIsFalse_DoesNotRegisterCleanup()
        {
            var engine = new TestBuildEngine();

            using var task = new RestoreTask
            {
                BuildEngine = engine,
                RestoreGraphItems = Array.Empty<ITaskItem>(),
                HideWarningsAndErrors = true,
                ResetProcessStateAfterBuild = false,
            };

            task.Execute();

            engine.RegisteredTaskObjects.Should().BeEmpty();
        }

        [Fact]
        public void RestoreTask_WhenResetProcessStateAfterBuildIsTrue_RegistersCleanup()
        {
            var engine = new TestBuildEngine();

            using var task = new RestoreTask
            {
                BuildEngine = engine,
                RestoreGraphItems = Array.Empty<ITaskItem>(),
                HideWarningsAndErrors = true,
                ResetProcessStateAfterBuild = true,
            };

            task.Execute();

            engine.RegisteredTaskObjects.Should().ContainSingle();
        }

        [Fact]
        public void ResetDefaultCredentialService_WhenCredentialServiceExternallyOwned_LeavesItIntact()
        {
            var original = HttpHandlerResourceV3.CredentialService;
            try
            {
                // Simulate an external owner (e.g. Visual Studio) that set the credential service while restore
                // did not create it.
                HttpHandlerResourceV3.CredentialService = null;
                DefaultCredentialServiceUtility.ResetDefaultCredentialService(); // clears restore's ownership flag

                var external = new Lazy<ICredentialService>(() => null);
                HttpHandlerResourceV3.CredentialService = external;

                DefaultCredentialServiceUtility.ResetDefaultCredentialService();

                HttpHandlerResourceV3.CredentialService.Should().BeSameAs(external);
            }
            finally
            {
                HttpHandlerResourceV3.CredentialService = original;
            }
        }

        [Fact]
        public void ResetDefaultCredentialService_WhenCreatedByRestore_ResetsToNull()
        {
            var original = HttpHandlerResourceV3.CredentialService;
            try
            {
                HttpHandlerResourceV3.CredentialService = null;
                DefaultCredentialServiceUtility.ResetDefaultCredentialService();
                HttpHandlerResourceV3.CredentialService = null;

                DefaultCredentialServiceUtility.SetupDefaultCredentialService(NullLogger.Instance, nonInteractive: true);
                HttpHandlerResourceV3.CredentialService.Should().NotBeNull();

                DefaultCredentialServiceUtility.ResetDefaultCredentialService();

                HttpHandlerResourceV3.CredentialService.Should().BeNull();
            }
            finally
            {
                HttpHandlerResourceV3.CredentialService = original;
            }
        }

        [Fact]
        public void PluginManager_ResetSharedInstance_ProducesFreshInstance()
        {
            var before = NuGet.Protocol.Plugins.PluginManager.Instance;

            NuGet.Protocol.Plugins.PluginManager.ResetSharedInstance();

            var after = NuGet.Protocol.Plugins.PluginManager.Instance;
            after.Should().NotBeSameAs(before);
        }
    }
}
