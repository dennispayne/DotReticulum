using Xunit.Abstractions;

namespace DotReticulum.Interfaces.Tests;

public sealed class RNodeHardwareFactAttribute : FactAttribute
{
    public RNodeHardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_PORT")))
            Skip = "Set DOTRETICULUM_RNODE_PORT to an explicitly authorized RNode port for read-only device queries.";
    }
}

[Trait("Category", "Unit")]
public sealed class Parity6RNodeHardwareTests(ITestOutputHelper output)
{
    [RNodeHardwareFact]
    public async Task AuthorizedSerialDeviceAnswersNativeDetectionWithoutRadioConfiguration()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var connection = await RNodeDevice.SerialFactory(Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_PORT")!)(deadline.Token);
        using var owner = connection.Owner;
        await using var stream = connection.Stream;
        using var interrupt = deadline.Token.Register(() => stream.Dispose());
        await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
        await stream.WriteAsync(RNodeProtocol.Detect(), deadline.Token);
        await stream.FlushAsync(deadline.Token);
        var frames = new List<RNodeFrame>();
        var decoder = new RNodeFrameDecoder();
        var buffer = new byte[256];
        while (!new byte[] { 0x08, 0x50, 0x48, 0x49 }.All(command => frames.Any(frame => frame.Command == command)))
        {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            Assert.True(count > 0, "The authorized serial device closed before returning identification.");
            frames.AddRange(decoder.Feed(buffer.AsSpan(0, count)));
        }
        Assert.Contains(frames, frame => frame.Command == 0x08 && frame.Payload.SequenceEqual(new byte[] { 0x46 }));
        var version = frames.Last(frame => frame.Command == 0x50).Payload;
        Assert.Equal(2, version.Length);
        Assert.True(version[0] > 1 || version[0] == 1 && version[1] >= 52, "RNode firmware is older than the supported 1.52 minimum.");
        var platform = frames.Last(frame => frame.Command == 0x48).Payload;
        var mcu = frames.Last(frame => frame.Command == 0x49).Payload;
        Assert.Single(platform);
        Assert.Single(mcu);
        output.WriteLine($"Read-only RNode detection: firmware {version[0]}.{version[1]}, platform 0x{platform[0]:X2}, MCU 0x{mcu[0]:X2}.");
        output.WriteLine("Only detect/version/platform/MCU queries were sent; radio configuration, RF, EEPROM and firmware were untouched.");
    }
}
