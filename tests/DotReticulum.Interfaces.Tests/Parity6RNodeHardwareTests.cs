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

public sealed class RNodeReceiveHardwareFactAttribute : FactAttribute
{
    public RNodeReceiveHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_RECEIVE_ONLY") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_PORT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_FREQUENCY")))
            Skip = "Receive-only radio startup requires explicit DOTRETICULUM_RNODE_RECEIVE_ONLY=1, authorized PORT and FREQUENCY.";
    }
}

[Trait("Category", "Integration")]
public sealed class Parity6RNodeHardwareTests(ITestOutputHelper output)
{
    [RNodeReceiveHardwareFact]
    public async Task AuthorizedReceiveOnlyRadioStartsReconfiguresDisablesAndReopensWithoutSendingPackets()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var frequency = long.Parse(Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_FREQUENCY")!,
            System.Globalization.CultureInfo.InvariantCulture);
        var options = new RNodeOptions
        {
            Reconnect = false,
            Radio = new RNodeRadioOptions
            {
                Frequency = frequency, Bandwidth = 125000, TxPower = 0, SpreadingFactor = 8, CodingRate = 6,
            },
        };
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var radio = new RNodePacketInterface(Environment.GetEnvironmentVariable("DOTRETICULUM_RNODE_PORT")!, options);
            radio.Faulted += faults.Enqueue;
            await radio.StartAsync(deadline.Token);
            Assert.True(radio.IsConnected);
            AssertEcho(radio, options.Radio);
            var changed = options.Radio with { SpreadingFactor = 9, CodingRate = 7 };
            await radio.ConfigureRadioAsync(changed, deadline.Token);
            AssertEcho(radio, changed);
            await radio.SetRadioStateAsync(false, deadline.Token);
            while (Convert.ToInt32(radio.GetDiagnostics()["radio_state"], System.Globalization.CultureInfo.InvariantCulture) != 0)
                await Task.Delay(20, deadline.Token);
            output.WriteLine($"Receive-only serial pass {attempt + 1}: startup/live echoes match, radio-off acknowledged, no SendAsync call.");
        }
        Assert.Empty(faults);
        output.WriteLine("Only volatile radio configuration/control was requested; this is not RF transmission or peer-receipt acceptance.");
    }

    private static void AssertEcho(RNodePacketInterface radio, RNodeRadioOptions expected)
    {
        var diagnostics = radio.GetDiagnostics();
        long Value(string name) => Convert.ToInt64(diagnostics[name], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(Value("frequency"), expected.Frequency - 100, expected.Frequency + 100);
        Assert.Equal(expected.Bandwidth, Value("bandwidth"));
        Assert.Equal(expected.TxPower, Value("txpower"));
        Assert.Equal(expected.SpreadingFactor, Value("spreadingfactor"));
        Assert.Equal(expected.CodingRate, Value("codingrate"));
        Assert.Equal(1, Value("radio_state"));
        Assert.True(radio.Bitrate > 0);
    }

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
