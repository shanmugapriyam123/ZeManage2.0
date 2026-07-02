using BIManage.Infrastructure.DependencyInjection;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Core
{
    public class BootstrapHealthTrackerTests
    {
        [Fact]
        public void Default_NeedsRecovery()
        {
            var tracker = new BootstrapHealthTracker();
            tracker.NeedsRecovery.Should().BeTrue();
            tracker.IsFullyHealthy.Should().BeFalse();
        }

        [Fact]
        public void AllFlagsSet_IsFullyHealthy()
        {
            var tracker = new BootstrapHealthTracker
            {
                PersistenceReady = true,
                ApiServicesReady = true,
                SignalRReady = true,
                LicenseValidated = true,
                SessionSynced = true
            };

            tracker.IsFullyHealthy.Should().BeTrue();
            tracker.NeedsRecovery.Should().BeFalse();
        }

        [Fact]
        public void MissingApiServices_NeedsRecovery()
        {
            var tracker = new BootstrapHealthTracker
            {
                PersistenceReady = true,
                ApiServicesReady = false,
                SessionSynced = true,
                LicenseValidated = true
            };

            tracker.NeedsRecovery.Should().BeTrue();
            tracker.IsFullyHealthy.Should().BeFalse();
        }

        [Fact]
        public void MaxRecoveryAttempts_StopsRetrying()
        {
            var tracker = new BootstrapHealthTracker { ApiServicesReady = false };
            tracker.ShouldAttemptRecovery.Should().BeTrue();

            tracker.RecoveryAttempts = BootstrapHealthTracker.MaxRecoveryAttempts;
            tracker.ShouldAttemptRecovery.Should().BeFalse("should stop after max attempts");
        }

        [Fact]
        public void GetStatusSummary_WhenHealthy_ReturnsHealthy()
        {
            var tracker = new BootstrapHealthTracker
            {
                PersistenceReady = true,
                ApiServicesReady = true,
                SessionSynced = true,
                LicenseValidated = true,
                SignalRReady = true
            };

            tracker.GetStatusSummary().Should().Be("All services healthy");
        }

        [Fact]
        public void GetStatusSummary_WhenUnhealthy_ListsIssues()
        {
            var tracker = new BootstrapHealthTracker
            {
                PersistenceReady = true,
                ApiServicesReady = false,
                LicenseValidated = false
            };

            var summary = tracker.GetStatusSummary();
            summary.Should().Contain("API services not available");
            summary.Should().Contain("License not validated");
        }

        [Fact]
        public void GetStatusSummary_IncludesLastError()
        {
            var tracker = new BootstrapHealthTracker
            {
                ApiServicesReady = false,
                LastError = "Connection refused"
            };

            tracker.GetStatusSummary().Should().Contain("Connection refused");
        }
    }
}
