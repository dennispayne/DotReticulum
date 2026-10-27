using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rnpath;
using DotReticulum.Applications.Rnprobe;
using DotReticulum.Applications.Rnstatus;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Unit")]
public sealed class Parity7NetworkUtilitiesTests
{
    private const string Revision = "a29bfb51c468945c96a1ee22cae0114e993e2391";
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity7-network-utilities.json");

    [Fact]
    public void HelpOptionsAreCapturedFromPinnedPythonUtilities()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        Assert.Equal(Revision, fixture.RootElement.GetProperty("revision").GetString());
        AssertHelpContract(fixture, "rnstatus", RnstatusApp.Usage,
            "259b187e6184c4a62fd824c83a6e0fa798bfcd0c165ec7c2f185751aeced3efd");
        AssertHelpContract(fixture, "rnpath", RnpathApp.Usage,
            "9674bdea46ed8c34fee3f4aafa9b675de51aa9e04bbee7cd27eb1f06f2c1ea2b");
        AssertHelpContract(fixture, "rnprobe", RnprobeApp.Usage,
            "adfba42a976f7e85152b815f2b87b7602fa15500351b87e33b48ab17a0cdef34");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("path")]
    [InlineData("probe")]
    public async Task HelpAndVersionAreSuccessfulWithoutStartingRuntime(string tool)
    {
        var run = tool switch
        {
            "status" => (Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>>)RnstatusApp.RunAsync,
            "path" => RnpathApp.RunAsync,
            _ => RnprobeApp.RunAsync,
        };
        var help = new StringWriter();
        Assert.Equal(0, await run(["--help"], help, TextWriter.Null, CancellationToken.None));
        Assert.Contains("--config", help.ToString());
        var version = new StringWriter();
        Assert.Equal(0, await run(["--version"], version, TextWriter.Null, CancellationToken.None));
        Assert.Contains(tool == "status" ? "rnstatus" : tool == "path" ? "rnpath" : "rnprobe", version.ToString());
    }

    [Fact]
    public async Task ProbeWithoutDestinationHashPrintsUsageLikePinnedCli()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var contract = fixture.RootElement.GetProperty("rnprobe_no_hash_contract");
        var output = new StringWriter();
        Assert.Equal(0, await RnprobeApp.RunAsync([], output, TextWriter.Null, CancellationToken.None));
        Assert.Equal(0, contract.GetProperty("exit_code").GetInt32());
        Assert.True(contract.GetProperty("has_usage").GetBoolean());
        Assert.True(contract.GetProperty("has_full_name").GetBoolean());
        Assert.True(contract.GetProperty("has_destination_hash").GetBoolean());
        Assert.Contains("Usage:", output.ToString());
        Assert.Contains("<full-name>", output.ToString());
        Assert.Contains("<destination-hash>", output.ToString());
    }

    [Fact]
    public void ProbeVerbosityPreservesRepeatedCount()
    {
        var options = RnprobeApp.Parse(["-v", "--verbose", "app.aspect", new string('0', 32)]);
        Assert.Equal(2, options.Verbosity);
        Assert.Equal(2, RnprobeApp.Parse(["-vv"]).Verbosity);
    }

    [Fact]
    public void StatusAnnounceSortUsesCombinedTrafficAndKeepsUnknownMeasurementsUnknown()
    {
        var low = new Dictionary<object, object?>
        {
            ["name"] = "low",
            ["incoming_announce_frequency"] = 2d,
            ["outgoing_announce_frequency"] = 1d,
        };
        var high = new Dictionary<object, object?>
        {
            ["name"] = "high",
            ["incoming_announce_frequency"] = 3d,
            ["outgoing_announce_frequency"] = 8d,
        };
        var unknown = new Dictionary<object, object?> { ["name"] = "unknown" };

        var sorted = RnstatusApp.Sort([low, high, unknown], "anns", reverse: false);

        Assert.Equal(new[] { "high", "low", "unknown" }, sorted.Select(row => (string)row["name"]!));
    }

    [Fact]
    public void StatusTotalsAndQueueOptionsRenderMeasuredRatesAndPressures()
    {
        var options = RnstatusApp.Parse(["--totals", "--pps", "--queues"]);
        var interfaceRow = new Dictionary<object, object?>
        {
            ["name"] = "Memory",
            ["mode"] = (byte)3,
            ["announces_to_internal"] = true,
        };
        var stats = new Dictionary<object, object?>
        {
            ["interfaces"] = new object?[] { interfaceRow },
            ["rxb"] = 250L,
            ["txb"] = 500L,
            ["rxs"] = 800d,
            ["txs"] = 1600d,
            ["rxpps"] = 3d,
            ["txpps"] = 4d,
            ["rxqt"] = 2,
            ["tqpressure"] = 0.5d,
            ["rxqtd"] = 1L,
            ["rxqd"] = 1,
            ["dqpressure"] = 0.25d,
            ["rxqdd"] = 0L,
            ["rxqa"] = 1,
            ["aqpressure"] = 0.25d,
            ["rxqad"] = 0L,
            ["rxqp"] = 0,
            ["pqpressure"] = 0d,
            ["rxqpd"] = 0L,
            ["rxqil"] = 0,
            ["ilqpressure"] = 0d,
            ["rxqild"] = 0L,
        };
        var output = new StringWriter();

        RnstatusApp.Render(stats, options, output);

        Assert.Contains("Rates", output.ToString());
        Assert.Contains("RX packets/s", output.ToString());
        Assert.Contains("Total queue", output.ToString());
        Assert.Contains("50.0% 2 packets, 1 dropped", output.ToString());
        Assert.Contains("Mode      : Access Point (a>i)", output.ToString());
    }

    [Fact]
    public void StatusRendersObservedAnnounceQueuesFiltersAndBearerDropsOnlyWhenPresent()
    {
        var options = RnstatusApp.Parse(["--announce-stats", "--queues", "--blocked-ips"]);
        var interfaceRow = new Dictionary<object, object?>
        {
            ["name"] = "Memory",
            ["gravity"] = 1,
            ["announce_queue"] = 2,
            ["held_announces"] = 1,
            ["announce_rate_target"] = 5d,
            ["announce_rate_penalty"] = 10d,
            ["announce_rate_grace"] = 3,
            ["packet_filter_hits"] = 3L,
            ["blocked_ips"] = 2,
            ["ifac_netname"] = "mesh",
            ["ifac_size"] = 8,
            ["ifac_signature"] = Enumerable.Range(0, 8).Select(value => (byte)value).ToArray(),
            ["txbuffered"] = 64,
            ["txdrp"] = 1,
            ["txdrb"] = 12,
            ["txstalled"] = true,
            ["tx_write_pending_bytes"] = 32L,
        };
        var output = new StringWriter();

        RnstatusApp.Render(new Dictionary<object, object?>
        {
            ["interfaces"] = new object?[] { interfaceRow },
        }, options, output);

        Assert.Contains("Announce queue", output.ToString());
        Assert.Contains("Held announces", output.ToString());
        Assert.Contains("Packet filter hits", output.ToString());
        Assert.Contains("Blocked IPs", output.ToString());
        Assert.Contains("TX buffered", output.ToString());
        Assert.Contains("TX drops", output.ToString());
        Assert.Contains("TX drop bytes", output.ToString());
        Assert.Contains("TX stalled", output.ToString());
        Assert.Contains("gravity 1", output.ToString());
        Assert.Contains("Announce limit", output.ToString());
        Assert.Contains("Network   : mesh", output.ToString());
        Assert.Contains("64-bit IFAC", output.ToString());
        Assert.Contains("Write pending bytes", output.ToString());
    }

    [Fact]
    public void PathTableTakesPrecedenceWhenRatesAndTableAreBothRequested()
    {
        var options = RnpathApp.Parse(["--table", "--rates"]);

        Assert.True(options.Table);
        Assert.False(options.Rates);
    }

    [Fact]
    public void PublishedBlackholeListRemainsTextWhenJsonWasRequested()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var source = fixture.RootElement.GetProperty("rnpath_blackhole_json_branch");
        var rows = new[]
        {
            new KeyValuePair<object, object?>(Convert.FromHexString(source.GetProperty("hash").GetString()!),
                new Dictionary<object, object?>
                {
                    ["source"] = Convert.FromHexString(source.GetProperty("source").GetString()!),
                    ["until"] = null,
                    ["reason"] = source.GetProperty("reason").GetString(),
                }),
        };
        var output = new StringWriter();
        RnpathApp.WriteBlackholes(rows, null, output);
        Assert.Equal(source.GetProperty("output").GetString() + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void HashInputsAreStrictlySizedAndHexadecimal()
    {
        var expected = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        Assert.Equal(expected, RnpathApp.ParseHash(Convert.ToHexString(expected)));
        Assert.Equal(expected, RnprobeApp.ParseHash(Convert.ToHexString(expected)));
        Assert.Throws<ArgumentException>(() => RnpathApp.ParseHash("00"));
        Assert.Throws<ArgumentException>(() => RnpathApp.ParseHash(new string('z', 32)));
        Assert.Throws<ArgumentException>(() => RnprobeApp.ParseHash(new string('0', 31)));
    }

    [Fact]
    public void PathAndRateJsonMatchPinnedPythonManagementRows()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var reference = fixture.RootElement.GetProperty("rnpath_reference_json");
        AssertRowsMatch(reference.GetProperty("path_rows"), reference.GetProperty("path_output"), rates: false);
        AssertRowsMatch(reference.GetProperty("rate_rows"), reference.GetProperty("rate_output"), rates: true);
    }

    [Fact]
    public async Task InvalidOptionValuesFailBeforeOpeningConfiguration()
    {
        var statusError = new StringWriter();
        Assert.Equal(1, await RnstatusApp.RunAsync(["-m", "-I", "NaN"], TextWriter.Null, statusError, CancellationToken.None));
        Assert.Contains("monitor interval", statusError.ToString(), StringComparison.OrdinalIgnoreCase);

        var pathError = new StringWriter();
        Assert.Equal(1, await RnpathApp.RunAsync(["-t", "-m", "-1"], TextWriter.Null, pathError, CancellationToken.None));
        Assert.Contains("Maximum hops", pathError.ToString());

        var probeError = new StringWriter();
        Assert.Equal(1, await RnprobeApp.RunAsync(["--size", "-1", "app.aspect", new string('0', 32)],
            TextWriter.Null, probeError, CancellationToken.None));
        Assert.Contains("nonnegative", probeError.ToString());
    }

    private static void AssertHelpContract(JsonDocument fixture, string tool, string usage, string sourceHash)
    {
        var item = fixture.RootElement.GetProperty("tools").GetProperty(tool);
        Assert.Equal(sourceHash, item.GetProperty("source_sha256").GetString());
        var upstreamHelp = item.GetProperty("help").GetString()!;
        foreach (Match option in Regex.Matches(upstreamHelp, @"(?<![\w-])--[a-z][a-z0-9-]*|(?<![\w-])-[A-Za-z](?![\w-])"))
            Assert.Contains(option.Value, usage, StringComparison.Ordinal);
    }

    private static void AssertRowsMatch(JsonElement sourceRows, JsonElement expected, bool rates)
    {
        var rows = new List<object?>();
        foreach (var source in sourceRows.EnumerateArray())
        {
            var row = new Dictionary<object, object?>();
            foreach (var property in source.EnumerateObject())
            {
                object? value = property.Name switch
                {
                    "hash" or "via" => Convert.FromHexString(property.Value.GetString()!),
                    "timestamps" => property.Value.EnumerateArray().Select(item => (object?)item.GetDouble()).ToArray(),
                    "hops" or "rate_violations" => property.Value.GetInt32(),
                    "interface" => property.Value.GetString(),
                    _ => property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetDouble() : property.Value.GetString(),
                };
                row[property.Name] = value;
            }
            rows.Add(row);
        }

        var actual = JsonNode.Parse(RnpathApp.WriteJson(rows.ToArray()));
        var expectedNode = JsonNode.Parse(expected.GetRawText());
        Assert.True(JsonNode.DeepEquals(expectedNode, actual),
            $"Expected pinned {(rates ? "rate" : "path")} JSON {expectedNode}; got {actual}.");
    }
}
