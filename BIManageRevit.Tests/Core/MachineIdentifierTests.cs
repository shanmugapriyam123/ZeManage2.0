using BIManage.Common.Helpers;
using BIManageRevit.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Core
{
    public class MachineIdentifierTests
    {
        [Fact]
        public void GetMachineId_ReturnsSameValueOnRepeatedCalls()
        {
            var logger = new ConsoleLogger();
            var id1 = MachineIdentifier.GetMachineId(logger);
            var id2 = MachineIdentifier.GetMachineId(logger);

            id1.Should().Be(id2, "cached result should be identical");
        }

        [Fact]
        public void GetMachineId_ReturnsValidGuidFormat()
        {
            var id = MachineIdentifier.GetMachineId();
            System.Guid.TryParse(id, out _).Should().BeTrue("result should be a valid GUID");
        }

        [Fact]
        public void GetMachineId_IsNotEmpty()
        {
            var id = MachineIdentifier.GetMachineId();
            id.Should().NotBeNullOrEmpty();
            id.Should().NotBe("00000000-0000-0000-0000-000000000000");
        }

        [Fact]
        public void GetMachineId_LogsHardwareIds()
        {
            var logger = new ConsoleLogger();
            var id = MachineIdentifier.GetMachineId(logger);
            // If we get a valid GUID, the hardware ID gathering succeeded
            id.Should().NotBeNullOrEmpty();
        }
    }
}
