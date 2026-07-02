using System;
using System.Collections.Generic;
using BIManage.Infrastructure.Api;
using BIManage.Licensing;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Core
{
    /// <summary>
    /// Tests license mode mapping from heartbeat response to LicenseInfo.
    /// Verifies the server's LicenseMode (0/1/2) maps correctly to client-side behavior.
    /// </summary>
    public class LicenseMappingTests
    {
        #region LicenseInfo Mode Computation

        [Fact]
        public void LicenseInfo_ValidStatus_ReturnsLicensedMode()
        {
            var info = new LicenseInfo { Status = LicenseStatus.Valid, IsPassiveMode = false };
            info.Mode.Should().Be(LicenseMode.Licensed);
        }

        [Fact]
        public void LicenseInfo_ValidStatus_WithPassive_ReturnsPassiveMode()
        {
            var info = new LicenseInfo { Status = LicenseStatus.Valid, IsPassiveMode = true };
            info.Mode.Should().Be(LicenseMode.Passive);
        }

        [Fact]
        public void LicenseInfo_ExpiredStatus_ReturnsBreachedMode()
        {
            var info = new LicenseInfo { Status = LicenseStatus.Expired };
            info.Mode.Should().Be(LicenseMode.Breached);
        }

        [Fact]
        public void LicenseInfo_RevokedStatus_ReturnsBreachedMode()
        {
            var info = new LicenseInfo { Status = LicenseStatus.Revoked };
            info.Mode.Should().Be(LicenseMode.Breached);
        }

        [Fact]
        public void LicenseInfo_InvalidStatus_ReturnsBreachedMode()
        {
            var info = new LicenseInfo { Status = LicenseStatus.Invalid };
            info.Mode.Should().Be(LicenseMode.Breached);
        }

        [Fact]
        public void LicenseInfo_GracePeriod_NotPassive_ReturnsLicensedMode()
        {
            var info = new LicenseInfo { Status = LicenseStatus.GracePeriod, IsPassiveMode = false };
            info.Mode.Should().Be(LicenseMode.Licensed);
        }

        #endregion

        #region Module Gating

        [Fact]
        public void Licensed_AllModulesInList_AllEnabled()
        {
            var info = new LicenseInfo
            {
                Status = LicenseStatus.Valid,
                IsPassiveMode = false,
                EnabledModules = new List<int> { 1, 2, 3, 4, 5 }
            };

            info.IsModuleEnabled(LicenseModule.Protection).Should().BeTrue();
            info.IsModuleEnabled(LicenseModule.ActivityTracker).Should().BeTrue();
            info.IsModuleEnabled(LicenseModule.HealthMonitor).Should().BeTrue();
            info.IsModuleEnabled(LicenseModule.SyncControl).Should().BeTrue();
            info.IsModuleEnabled(LicenseModule.AI).Should().BeTrue();
        }

        [Fact]
        public void Passive_OnlyProtectionAndActivityTracker()
        {
            var info = new LicenseInfo
            {
                Status = LicenseStatus.Valid,
                IsPassiveMode = true,
                EnabledModules = new List<int> { 1, 2, 3, 4, 5 }
            };

            info.IsModuleEnabled(LicenseModule.Protection).Should().BeTrue("Protection stays active in Passive");
            info.IsModuleEnabled(LicenseModule.ActivityTracker).Should().BeTrue("ActivityTracker stays active in Passive");
            info.IsModuleEnabled(LicenseModule.HealthMonitor).Should().BeFalse("disabled in Passive");
            info.IsModuleEnabled(LicenseModule.SyncControl).Should().BeFalse("disabled in Passive");
            info.IsModuleEnabled(LicenseModule.AI).Should().BeFalse("disabled in Passive");
        }

        [Fact]
        public void Breached_AllModulesDisabled()
        {
            var info = new LicenseInfo
            {
                Status = LicenseStatus.Expired,
                EnabledModules = new List<int> { 1, 2, 3, 4, 5 }
            };

            info.IsModuleEnabled(LicenseModule.Protection).Should().BeFalse();
            info.IsModuleEnabled(LicenseModule.ActivityTracker).Should().BeFalse();
            info.IsModuleEnabled(LicenseModule.HealthMonitor).Should().BeFalse();
            info.IsModuleEnabled(LicenseModule.SyncControl).Should().BeFalse();
            info.IsModuleEnabled(LicenseModule.AI).Should().BeFalse();
        }

        [Fact]
        public void Licensed_ModuleNotInList_Disabled()
        {
            var info = new LicenseInfo
            {
                Status = LicenseStatus.Valid,
                IsPassiveMode = false,
                EnabledModules = new List<int> { 1, 2 } // Only Protection + ActivityTracker purchased
            };

            info.IsModuleEnabled(LicenseModule.Protection).Should().BeTrue();
            info.IsModuleEnabled(LicenseModule.ActivityTracker).Should().BeTrue();
            info.IsModuleEnabled(LicenseModule.HealthMonitor).Should().BeFalse("not purchased");
            info.IsModuleEnabled(LicenseModule.AI).Should().BeFalse("not purchased");
        }

        #endregion

        #region CreateDefault

        [Fact]
        public void CreateDefault_HasUnknownStatus()
        {
            var info = LicenseInfo.CreateDefault();
            info.Status.Should().Be(LicenseStatus.Unknown);
            info.IsPassiveMode.Should().BeFalse();
            info.EnabledModules.Should().BeEmpty();
        }

        #endregion

        #region SeatUsageDisplay

        [Fact]
        public void SeatUsageDisplay_FormatsCorrectly()
        {
            var info = new LicenseInfo { ActiveSeats = 5, TotalSeats = 10 };
            info.SeatUsageDisplay.Should().Be("5 / 10 seats active");
        }

        #endregion
    }
}
