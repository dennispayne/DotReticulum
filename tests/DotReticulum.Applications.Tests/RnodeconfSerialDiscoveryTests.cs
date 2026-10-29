using DotReticulum.Applications.Rnodeconf;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Unit")]
public sealed class RnodeconfSerialDiscoveryTests
{
    [Fact]
    public void CompositePortUsesPhysicalUsbSerialAcrossApplicationAndBootloaderIdentities()
    {
        foreach (var product in new[] { "8029", "0029" })
        {
            var port = $@"USB\VID_239A&PID_{product}&MI_00\9&LOCATION&1&0000";
            var device = $@"USB\VID_239A&PID_{product}\DEVICE-SERIAL";
            Assert.Equal("DEVICE-SERIAL", RnodeconfSerialDiscovery.WindowsUsbSerial(port,
                id => id == port ? device : null));
        }
        Assert.Equal("DEVICE-SERIAL", RnodeconfSerialDiscovery.WindowsUsbSerial(
            @"USB\VID_239A&PID_8029\DEVICE-SERIAL", _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void MissingSerialDoesNotUseLocationOrRootHubAsIdentityAndTraversalIsBounded()
    {
        var port = @"USB\VID_239A&PID_8029&MI_00\9&LOCATION&1&0000";
        var hub = @"USB\ROOT_HUB30\HUB";
        Assert.Null(RnodeconfSerialDiscovery.WindowsUsbSerial(port, id => id == port ? hub : null));
        Assert.Null(RnodeconfSerialDiscovery.WindowsUsbSerial(port, _ => port));
        var calls = 0;
        Assert.Null(RnodeconfSerialDiscovery.WindowsUsbSerial(port, _ => $"node-{++calls}"));
        Assert.Equal(8, calls);
    }
}
