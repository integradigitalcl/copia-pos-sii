using GrunflexPOS.Web.Services.Licensing;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class CajaSlotGateTests
{
    [Theory]
    [InlineData(3, true, 3)]
    [InlineData(0, true, 5)]
    [InlineData(0, false, 1)]
    [InlineData(8, false, 8)]
    public void ResolveMaxBoxes_ReturnsExpected(int numberOfBoxes, bool multicaja, int expected) =>
        Assert.Equal(expected, CajaSlotGate.ResolveMaxBoxes(numberOfBoxes, multicaja));

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void CanEnableMulticaja_RespectsLicense(bool multicajaLicensed, bool requireLicense, bool expected) =>
        Assert.Equal(expected, CajaSlotGate.CanEnableMulticaja(multicajaLicensed, requireLicense));
}
