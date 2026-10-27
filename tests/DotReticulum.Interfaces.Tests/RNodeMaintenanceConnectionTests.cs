using DotReticulum.Interfaces;

namespace DotReticulum.Interfaces.Tests;

[Trait("Category", "Unit")]
public sealed class RNodeMaintenanceConnectionTests
{
    [Fact]
    public async Task RejectsNetworkLocatorWithoutOpeningHardware() =>
        await Assert.ThrowsAsync<NotSupportedException>(async () => await RNodeMaintenanceConnection.OpenSerialAsync("tcp://127.0.0.1:1"));

    [Fact]
    public async Task CancellationBeforeOpenDoesNotAccessHardware()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await RNodeMaintenanceConnection.OpenSerialAsync("NOT-A-DEVICE", cancellation.Token));
    }
}
